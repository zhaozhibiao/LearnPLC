// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Autonomy®

#include "../plc_app/debug_write_journal.h"
#include "../plc_app/image_tables.h"
#include "../plc_app/journal_buffer.h"
#include "../plc_app/plc_state_manager.h"
#include "../plc_app/plc_switch.h"
#include "../plc_app/unix_socket.h"
#include "../plc_app/utils/log.h"
#include "../plc_app/utils/utils.h"
#include "plugin_config.h"
#include "plugin_driver.h"
#include "vpp_plugin_seal.h"
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

// External buffer declarations from image_tables.c
extern IEC_BOOL *bool_input[BUFFER_SIZE][8];
extern IEC_BOOL *bool_output[BUFFER_SIZE][8];
extern IEC_BYTE *byte_input[BUFFER_SIZE];
extern IEC_BYTE *byte_output[BUFFER_SIZE];
extern IEC_UINT *int_input[BUFFER_SIZE];
extern IEC_UINT *int_output[BUFFER_SIZE];
extern IEC_UDINT *dint_input[BUFFER_SIZE];
extern IEC_UDINT *dint_output[BUFFER_SIZE];
extern IEC_ULINT *lint_input[BUFFER_SIZE];
extern IEC_ULINT *lint_output[BUFFER_SIZE];
extern IEC_UINT *int_memory[BUFFER_SIZE];
extern IEC_UDINT *dint_memory[BUFFER_SIZE];
extern IEC_ULINT *lint_memory[BUFFER_SIZE];
extern IEC_BOOL *bool_memory[BUFFER_SIZE][8];

// Prototypes

// Driver management functions
plugin_driver_t *plugin_driver_create(void)
{
    plugin_driver_t *driver = calloc(1, sizeof(plugin_driver_t));
    if (!driver)
    {
        return NULL;
    }

    // Buffer access no longer uses a driver-owned mutex: reads go through the
    // runtime's image_lock()/image_unlock() (flush-on-lock) and writes through
    // the lock-free journal. Nothing to initialize here.
    return driver;
}

// Journal write wrapper functions for plugins
// These match the function pointer signatures in plugin_types.h and delegate
// to the journal_buffer.h API with proper type casting.

static int plugin_journal_write_bool(int type, int index, int bit, int value)
{
    return journal_write_bool((journal_buffer_type_t)type, (uint16_t)index, (uint8_t)bit,
                              value != 0);
}

static int plugin_journal_write_byte(int type, int index, int value)
{
    return journal_write_byte((journal_buffer_type_t)type, (uint16_t)index, (uint8_t)value);
}

static int plugin_journal_write_int(int type, int index, int value)
{
    return journal_write_int((journal_buffer_type_t)type, (uint16_t)index, (uint16_t)value);
}

static int plugin_journal_write_dint(int type, int index, unsigned int value)
{
    return journal_write_dint((journal_buffer_type_t)type, (uint16_t)index, (uint32_t)value);
}

static int plugin_journal_write_lint(int type, int index, unsigned long long value)
{
    return journal_write_lint((journal_buffer_type_t)type, (uint16_t)index, (uint64_t)value);
}

// STruC++ debugger thunks forwarding to ext_strucpp_debug_* loaded from
// the program .so. All tolerate ext_*==NULL (no program) and return a
// safe sentinel.

static uint8_t plugin_debug_array_count(void)
{
    return ext_strucpp_debug_array_count ? ext_strucpp_debug_array_count() : 0;
}

static uint16_t plugin_debug_elem_count(uint8_t arr)
{
    return ext_strucpp_debug_elem_count ? ext_strucpp_debug_elem_count(arr) : 0;
}

static uint16_t plugin_debug_size(uint8_t arr, uint16_t elem)
{
    return ext_strucpp_debug_size ? ext_strucpp_debug_size(arr, elem) : 0;
}

static uint16_t plugin_debug_read(uint8_t arr, uint16_t elem, uint8_t *dest)
{
    return ext_strucpp_debug_read ? ext_strucpp_debug_read(arr, elem, dest) : 0;
}

// debug_set/debug_write run on PLUGIN threads. ENQUEUE through the
// debug-write journal; dispatcher applies at the no-task-running window.
// Returns 0x7E ok, 0x82 queue full, 0x81 no program.

static uint8_t plugin_debug_set(uint8_t arr, uint16_t elem, bool forcing,
                                const uint8_t *bytes, uint16_t len)
{
    if (!ext_strucpp_debug_set) return 0x81; // no program loaded
    uint8_t op = forcing ? (uint8_t)DBGW_OP_FORCE : (uint8_t)DBGW_OP_UNFORCE;
    int rc = runtime_external_write(arr, elem, op,
                                    forcing ? bytes : NULL,
                                    forcing ? len : 0);
    return (rc == 0) ? 0x7E : 0x82;
}

static uint8_t plugin_debug_write(uint8_t arr, uint16_t elem,
                                  const uint8_t *bytes, uint16_t len)
{
    if (!ext_strucpp_debug_write) return 0x81; // no program loaded
    int rc = runtime_external_write(arr, elem, (uint8_t)DBGW_OP_WRITE, bytes, len);
    return (rc == 0) ? 0x7E : 0x82;
}

// Plugin-invoked async PLC stop. Routes through the same transition
// path as the socket STOP; non-blocking. The claim is atomic under the
// state lock, so no pre-check here.
static void plugin_request_plc_stop(const char *reason)
{
    log_info("[PLUGIN] stop requested: %s", reason ? reason : "(no reason given)");
    if (!plc_begin_transition(PLC_STATE_STOPPED))
    {
        // Already stopping/stopped, or another transition is in flight;
        // the switch-movement reconciliation picks it up later (see
        // transition_worker in unix_socket.c), so a switch-driven stop cannot be
        // lost here.
        log_warn("[PLUGIN] stop request collapsed (already transitioning or not running)");
    }
}

// Plugin-invoked PLC start. Gated on the hardware mode switch: a plugin
// cannot start a PLC whose switch reads STOP.
static void plugin_request_plc_start(const char *reason)
{
    if (!plc_switch_allows_run())
    {
        log_warn("[PLUGIN] start request refused — mode switch is in STOP (%s)",
                 reason ? reason : "(no reason given)");
        return;
    }

    log_info("[PLUGIN] start requested: %s", reason ? reason : "(no reason given)");
    if (!plc_begin_transition(PLC_STATE_RUNNING))
    {
        log_warn("[PLUGIN] start request collapsed (already transitioning or already running)");
    }
}

// Thin adapter so plugin_types.h can declare the position as a plain int and
// stay free of the plc_app include tree.
static void plugin_set_switch_position(int position)
{
    plc_set_switch_position(position == PLC_SWITCH_STOP ? PLC_SWITCH_STOP : PLC_SWITCH_RUN);
}

// Map PLCState to FC 0x49 LED values (0 STOPPED, 1 RUNNING, 2 ERROR).
// INIT/EMPTY report as STOPPED (not executing).
static int plugin_get_plc_state(void)
{
    switch (plc_get_state())
    {
    case PLC_STATE_RUNNING:
        return 1;
    case PLC_STATE_ERROR:
        return 2;
    default:
        return 0;
    }
}

// Python capsule destructor for runtime args
// Breakpoint here to debug capsule issues


// Create Python capsule with runtime arguments


/* Tear down one plugin instance (cleanup hook, dlclose, free). Dispatch
 * on the slot's CURRENT stored type, not the incoming config's, so a
 * reordered or type-swapped plugins.conf does not leak handles. Caller
 * must ensure the plugin is not running. */
static void teardown_plugin_instance(plugin_instance_t *plugin)
{
    if (!plugin || plugin->running) return;
    if (plugin->native_plugin) {
        if (plugin->initialized && plugin->native_plugin->cleanup) plugin->native_plugin->cleanup();
        if (plugin->native_plugin->handle) dlclose(plugin->native_plugin->handle);
        free(plugin->native_plugin);
    }
    memset(plugin, 0, sizeof(*plugin));
}

int plugin_driver_native_config_supported(const plugin_config_t *configs, int count)
{
    if (count < 0 || count > MAX_PLUGINS || (!configs && count)) return -1;
    for (int i = 0; i < count; ++i) {
        if (configs[i].type != PLUGIN_TYPE_NATIVE &&
            (configs[i].type != PLUGIN_TYPE_PYTHON || configs[i].enabled)) {
            log_error("[PLUGIN] Unsupported enabled plugin '%s': this Runtime only loads native C/C++ plugins", configs[i].name);
            return -1;
        }
    }
    return 0;
}

int plugin_driver_update_config(plugin_driver_t *driver, const char *config_file)
{
    if (!driver || !config_file)
    {
        return -1;
    }

    // Check if config file exists, if not copy from default
    if (access(config_file, F_OK) != 0)
    {
        log_info("Config file %s not found, copying from plugins_default.conf", config_file);

        // Check if default config exists
        if (access("plugins_default.conf", F_OK) != 0)
        {
            log_error("Default config file plugins_default.conf not found");
            return -1;
        }

        // Copy default config to target config file
        FILE *src = fopen("plugins_default.conf", "r");
        FILE *dst = fopen(config_file, "w");

        if (!src || !dst)
        {
            log_error("Failed to copy default config");
            if (src)
                fclose(src);
            if (dst)
                fclose(dst);
            return -1;
        }

        char buffer[1024];
        size_t bytes;
        while ((bytes = fread(buffer, 1, sizeof(buffer), src)) > 0)
        {
            fwrite(buffer, 1, bytes, dst);
        }

        fclose(src);
        fclose(dst);
        log_info("Successfully copied default config to %s", config_file);
    }

    plugin_config_t configs[MAX_PLUGINS];
    int config_count = parse_plugin_config(config_file, configs, MAX_PLUGINS);
    if (config_count < 0)
    {
        return -1;
    }


    if (plugin_driver_native_config_supported(configs, config_count) != 0) return -1;
    for (int i = 0; i < driver->plugin_count; ++i) {
        if (driver->plugins[i].running) { log_error("[PLUGIN] Cannot replace a running plugin"); return -1; }
    }
    for (int i = 0; i < driver->plugin_count; ++i) teardown_plugin_instance(&driver->plugins[i]);
    driver->plugin_count = config_count;
    for (int i = 0; i < config_count; ++i) {
        plugin_instance_t *plugin = &driver->plugins[i];
        memcpy(&plugin->config, &configs[i], sizeof(plugin_config_t));
        if (configs[i].type != PLUGIN_TYPE_NATIVE) continue; /* explicitly disabled legacy entry */
        if (native_plugin_get_symbols(plugin) != 0) {
            plugin->degraded = plugin->config.enabled != 0;
            log_warn("[PLUGIN] Native plugin '%s' unavailable%s", plugin->config.name, plugin->config.enabled ? " (enabled)" : " (disabled)");
        }
    }
    return 0;
}

int plugin_driver_append_config(plugin_driver_t *driver, const char *config_file)
{
    if (!driver || !config_file)
    {
        return -1;
    }

    if (access(config_file, F_OK) != 0)
    {
        /* File absent is not an error — VPP is optional. */
        return 0;
    }

    plugin_config_t configs[MAX_PLUGINS];
    /* This config file comes from the user's upload (the editor writes it and
     * webserver/plcapp_management.py copies it verbatim), so its paths are
     * parsed under containment: no "..", no absolute paths. */
    int config_count = parse_plugin_config_contained(config_file, configs, MAX_PLUGINS);
    if (config_count < 0)
    {
        return -1;
    }

    if (plugin_driver_native_config_supported(configs, config_count) != 0) return -1;
    int degraded_count = 0;

    for (int w = 0; w < config_count; w++)
    {
        /* Skip if we'd overflow the driver's plugin array. */
        if (driver->plugin_count >= MAX_PLUGINS)
        {
            log_warn("[PLUGIN] plugin_driver_append_config: MAX_PLUGINS reached, skipping '%s'",
                     configs[w].name);
            break;
        }

        plugin_instance_t *plugin = &driver->plugins[driver->plugin_count];
        memset(plugin, 0, sizeof(*plugin));
        memcpy(&plugin->config, &configs[w], sizeof(plugin_config_t));
        driver->plugin_count++;

        if (configs[w].type == PLUGIN_TYPE_NATIVE)
        {
            if (native_plugin_get_symbols(plugin) != 0)
            {
                if (plugin->config.enabled)
                {
                    /* Fail-safe: degrade rather than abort (see
                     * plugin_driver_update_config for rationale). */
                    log_error("[PLUGIN] enabled VPP plugin '%s' failed to load symbols "
                              "- continuing without it (plugin unavailable)",
                              configs[w].name);
                    plugin->degraded = 1;
                    ++degraded_count;
                }
                else
                {
                    log_warn("[PLUGIN] disabled VPP plugin '%s' has no loadable .so",
                             configs[w].name);
                }
            }
        }
    }

    if (degraded_count > 0)
    {
        log_warn("[PLUGIN] %d enabled VPP plugin(s) failed to load and are unavailable; "
                 "the runtime will continue without them",
                 degraded_count);
    }

    /* Failed symbol loads are non-fatal; the runtime continues without them. */
    return 0;
}

int plugin_driver_load_config(plugin_driver_t *driver, const char *config_file)
{
    if (!driver || !config_file)
    {
        return -1;
    }

    /* update_config does the full teardown + rebuild including symbol
     * loading, so just forward. Re-calling get_symbols here would leak
     * the freshly-allocated bundles. */
    return plugin_driver_update_config(driver, config_file);
}

// Send to plugin init function all args
int plugin_driver_init(plugin_driver_t *driver)
{
    if (!driver) return -1;
    for (int i = 0; i < driver->plugin_count; ++i) {
        plugin_instance_t *plugin = &driver->plugins[i];
        if (plugin->config.type != PLUGIN_TYPE_NATIVE) { if (plugin->config.enabled) return -1; continue; }
        if (!plugin->native_plugin || !plugin->native_plugin->init) continue;
        plugin_runtime_args_t *args = generate_structured_args_with_driver(PLUGIN_TYPE_NATIVE, driver, i);
        if (!args) return -1;
        int result = plugin->native_plugin->init(args);
        free_structured_args(args);
        if (result != 0) { log_error("Native init function failed for plugin: %s (returned %d)", plugin->config.name, result); return -1; }
        plugin->initialized = 1;
    }
    return 0;
}

int plugin_driver_cleanup_init(plugin_driver_t *driver)
{
    if (!driver) return 0;
    int cleaned = 0;
    for (int i = driver->plugin_count - 1; i >= 0; --i) {
        plugin_instance_t *plugin = &driver->plugins[i];
        if (!plugin->initialized) continue;
        if (plugin->native_plugin && plugin->native_plugin->cleanup) plugin->native_plugin->cleanup();
        plugin->initialized = 0;
        ++cleaned;
    }
    return cleaned;
}



// Call the thread function for each plugin
int plugin_driver_start(plugin_driver_t *driver)
{
    if (!driver) return -1;
    int failed = 0;
    for (int i = 0; i < driver->plugin_count; ++i) {
        plugin_instance_t *plugin = &driver->plugins[i];
        if (!plugin->config.enabled) continue;
        if (plugin->config.type != PLUGIN_TYPE_NATIVE) return -1;
        if (!plugin->initialized || !plugin->native_plugin) { failed = 1; continue; }
        if (plugin->native_plugin->start) {
            if (plugin->native_plugin->start() != 0) { failed = 1; continue; }
            plugin->running = 1;
        }
    }
    return failed ? -1 : 0;
}

int plugin_driver_stop(plugin_driver_t *driver)
{
    if (!driver) return -1;
    int failed = 0;
    for (int i = 0; i < driver->plugin_count; ++i) {
        plugin_instance_t *plugin = &driver->plugins[i];
        if (!plugin->running) continue;
        if (!plugin->native_plugin || !plugin->native_plugin->stop) { failed = 1; continue; }
        plugin->native_plugin->stop();
        plugin->running = 0;
    }
    return failed ? -1 : 0;
}

void plugin_driver_destroy(plugin_driver_t *driver)
{
    if (!driver) return;
    if (plugin_driver_stop(driver) != 0) {
        log_error("[PLUGIN] Cannot unload a plugin whose stop is unconfirmed");
        return;
    }
    for (int i = driver->plugin_count - 1; i >= 0; --i) teardown_plugin_instance(&driver->plugins[i]);
    free(driver);
}

// Runtime arguments generation functions

/**
 * @brief Generate structured arguments for plugin initialization
 *
 * This function creates a structured argument containing all runtime buffers,
 * mutex functions, and metadata needed by external plugins.
 *
 * @param type Type of plugin (PLUGIN_TYPE_PYTHON or PLUGIN_TYPE_NATIVE)
 * @param driver Pointer to plugin driver (for buffer mutex)
 * @return Pointer to allocated structure/capsule, or NULL on error
 *
 * For PLUGIN_TYPE_NATIVE: Returns plugin_runtime_args_t*
 */
void *generate_structured_args_with_driver(plugin_type_t type, plugin_driver_t *driver,
                                           int plugin_index)
{
    log_debug("Generating structured args for plugin type %d", type);

    if (!driver || type != PLUGIN_TYPE_NATIVE || plugin_index < 0 || plugin_index >= driver->plugin_count)
    {
        log_error("Error - driver is NULL");
        return NULL;
    }

    plugin_runtime_args_t *args = malloc(sizeof(plugin_runtime_args_t));
    if (!args)
    {
        log_error("Error - failed to allocate memory for runtime args");
        return NULL;
    }

    log_debug("Allocated runtime args structure (size: %zu bytes)", sizeof(plugin_runtime_args_t));

    // Initialize all buffer pointers
    args->bool_input  = bool_input;
    args->bool_output = bool_output;
    args->byte_input  = byte_input;
    args->byte_output = byte_output;
    args->int_input   = int_input;
    args->int_output  = int_output;
    args->dint_input  = dint_input;
    args->dint_output = dint_output;
    args->lint_input  = lint_input;
    args->lint_output = lint_output;
    args->int_memory  = int_memory;
    args->dint_memory = dint_memory;
    args->lint_memory = lint_memory;
    args->bool_memory = bool_memory;

    // Flush-on-lock image read API (image mutex + journal drain). Points
    // directly at the runtime's image_tables entries; writes use the journal.
    args->image_lock   = image_lock;
    args->image_unlock = image_unlock;
    // STruC++ debugger surface — replaces the MatIEC-era
    // get_var_list / get_var_size / get_var_count flat-index API.
    // Each thunk forwards to the corresponding ext_strucpp_debug_*
    // function pointer resolved from the program .so. NULL-safe.
    args->debug_array_count = plugin_debug_array_count;
    args->debug_elem_count  = plugin_debug_elem_count;
    args->debug_size        = plugin_debug_size;
    args->debug_read        = plugin_debug_read;
    args->debug_set         = plugin_debug_set;
    args->debug_write       = plugin_debug_write;

    // Initialize plugin specific config path as empty
    memset(args->plugin_specific_config_file_path, '\0',
           sizeof(args->plugin_specific_config_file_path));

    memcpy(args->plugin_specific_config_file_path,
           driver->plugins[plugin_index].config.plugin_related_config_path,
           sizeof(driver->plugins[plugin_index].config.plugin_related_config_path));

    // Initialize buffer size info
    args->buffer_size     = BUFFER_SIZE;
    args->bits_per_buffer = 8;

    // Initialize logging functions
    args->log_info  = log_info;
    args->log_debug = log_debug;
    args->log_warn  = log_warn;
    args->log_error = log_error;

    // Initialize journal write functions for race-condition-free buffer writes
    args->journal_write_bool = plugin_journal_write_bool;
    args->journal_write_byte = plugin_journal_write_byte;
    args->journal_write_int  = plugin_journal_write_int;
    args->journal_write_dint = plugin_journal_write_dint;
    args->journal_write_lint = plugin_journal_write_lint;

    // Plugin-initiated async PLC stop (for unrecoverable hardware faults).
    args->request_plc_stop = plugin_request_plc_stop;

    // Run/stop control: the same transition path the socket START / STOP
    // commands drive, plus the mode-switch store the runtime gates starts on
    // and the state read a plugin uses to drive a status LED.
    args->request_plc_start   = plugin_request_plc_start;
    args->set_switch_position = plugin_set_switch_position;
    args->get_plc_state       = plugin_get_plc_state;

    // PLC base tick time. Runtime owns base_tick_ns; on first plugin init
    // (before symbols_init) it carries the 20 ms default, so plugins must
    // guard against the value being smaller than their needed resolution.
    args->base_tick_ns = base_tick_ns;

    // Validate critical pointers
    if (!args->image_lock || !args->image_unlock)
    {
        log_error("Error - image lock function pointers are NULL");
        free(args);
        return NULL;
    }

    return args;
}

// Free structured arguments
void free_structured_args(plugin_runtime_args_t *args)
{
    if (args)
    {
        // No dynamic allocations inside the structure to free
        // Just free the main structure
        free(args);
    }
}



int native_plugin_get_symbols(plugin_instance_t *plugin)
{
    if (!plugin || plugin->config.path[0] == '\0')
    {
        return -1;
    }

    // Allocate native plugin function bundle
    plugin_funct_bundle_t *native_bundle = calloc(1, sizeof(plugin_funct_bundle_t));
    if (!native_bundle)
    {
        return -1;
    }

    /* A VPP .so must match the hash compile.sh sealed when it built
     * that .so on this device, so a post-compile swap is refused.
     * Scoped to build/vpp/; built-in plugins are not sealed. */
    if (vpp_plugin_seal_required(plugin->config.path) &&
        vpp_plugin_seal_verify(plugin->config.path) != 0)
    {
        free(native_bundle);
        return -1;
    }

    // Load the shared library
    void *handle = dlopen(plugin->config.path, RTLD_LOCAL | RTLD_NOW);
    if (!handle)
    {
        const char *err = dlerror();
        log_error("Failed to load native plugin '%s': %s", plugin->config.path,
                  err ? err : "unknown error");
#if defined(__CYGWIN__) || defined(_WIN32)
        if (strstr(plugin->config.name, "ethercat") != NULL)
        {
            log_error("The EtherCAT plugin requires Npcap (https://npcap.com) to access "
                      "the network interface. Please install Npcap and restart the runtime.");
        }
#endif
        free(native_bundle);
        return -1;
    }

    // Store the handle in the native bundle
    native_bundle->handle = handle;

    // Clear any existing error
    dlerror();

    // Get function pointers for required functions
    // init function is required
    native_bundle->init = (plugin_init_func_t)dlsym(handle, "init");
    if (!native_bundle->init)
    {
        log_error("Error: 'init' function not found in native plugin '%s': %s", plugin->config.path,
                  dlerror());
        dlclose(handle);
        free(native_bundle);
        return -1;
    }

    // Optional functions - set to NULL if not found
    native_bundle->start = (plugin_start_loop_func_t)dlsym(handle, "start_loop");
    if (!native_bundle->start)
    {
        log_warn("'start_loop' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->stop = (plugin_stop_loop_func_t)dlsym(handle, "stop_loop");
    if (!native_bundle->stop)
    {
        log_warn("'stop_loop' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->cycle_start = (plugin_cycle_start_func_t)dlsym(handle, "cycle_start");
    if (!native_bundle->cycle_start)
    {
        log_warn("'cycle_start' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->cycle_end = (plugin_cycle_end_func_t)dlsym(handle, "cycle_end");
    if (!native_bundle->cycle_end)
    {
        log_warn("'cycle_end' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->cleanup = (plugin_cleanup_func_t)dlsym(handle, "cleanup");
    if (!native_bundle->cleanup)
    {
        log_warn("'cleanup' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->execute_command =
        (plugin_execute_command_func_t)dlsym(handle, "execute_command");
    if (!native_bundle->execute_command)
    {
        log_warn("'execute_command' function not found in native plugin '%s' (optional)",
                 plugin->config.path);
    }

    native_bundle->get_stats = (plugin_get_stats_func_t)dlsym(handle, "get_stats");
    // get_stats is fully optional — plugins that don't publish statistics
    // simply don't export it. No warning.

    // Optional retain store. A plugin exporting both save and load
    // becomes a candidate; see plugin_driver_find_retain_store. No
    // warning when absent — most plugins have no retention role.
    native_bundle->retain_save  = (plugin_retain_save_func_t)dlsym(handle, "retain_save");
    native_bundle->retain_load  = (plugin_retain_load_func_t)dlsym(handle, "retain_load");
    native_bundle->retain_flush = (plugin_retain_flush_func_t)dlsym(handle, "retain_flush");

    // Store the native bundle and handle in the plugin instance
    plugin->native_plugin = native_bundle;

    log_info("Native plugin '%s' symbols loaded successfully", plugin->config.path);
    log_info("  - init: (PASS)");
    log_info("  - start_loop: %s", native_bundle->start ? "(PASS)" : "(FAIL)");
    log_info("  - stop_loop: %s", native_bundle->stop ? "(PASS)" : "(FAIL)");
    log_info("  - cycle_start: %s", native_bundle->cycle_start ? "(PASS)" : "(FAIL)");
    log_info("  - cycle_end: %s", native_bundle->cycle_end ? "(PASS)" : "(FAIL)");
    log_info("  - cleanup: %s", native_bundle->cleanup ? "(PASS)" : "(FAIL)");
    log_info("  - execute_command: %s", native_bundle->execute_command ? "(PASS)" : "(FAIL)");
    log_info("  - get_stats: %s", native_bundle->get_stats ? "(PASS)" : "(FAIL)");

    return 0;
}

// Python plugin cycle function


// Retain store
static bool plugin_provides_retain_store(const plugin_instance_t *p)
{
    if (!p) return false;
    // Disabled plugins never serve as a store: they resolve symbols but
    // do not run, so they would accept save() and lose everything.
    if (!p->config.enabled) return false;
    // Save without load is worse than none; require both halves.
    return p->native_plugin && p->native_plugin->retain_save && p->native_plugin->retain_load;
}

plugin_instance_t *plugin_driver_find_retain_store(plugin_driver_t *driver)
{
    if (!driver) return NULL;

    plugin_instance_t *chosen = NULL;
    for (int i = 0; i < driver->plugin_count; i++)
    {
        plugin_instance_t *p = &driver->plugins[i];
        if (p->degraded || !plugin_provides_retain_store(p)) continue;

        if (!chosen)
        {
            chosen = p;
            continue;
        }
        // Two stores would both appear to work and disagree on the next boot,
        // which is a worse failure than refusing the second. First wins, and
        // the rest are named so the misconfiguration is visible.
        log_warn("Retain: plugin '%s' also provides retain storage; ignoring it — "
                 "'%s' was found first",
                 p->config.name, chosen->config.name);
    }
    return chosen;
}

int plugin_driver_retain_save(plugin_instance_t *store, const uint8_t *blob, uint16_t len)
{
    if (!plugin_provides_retain_store(store)) return -1;
    return store->native_plugin->retain_save(blob, len);
}

int plugin_driver_retain_load(plugin_instance_t *store, const char *program_md5, uint16_t md5_len,
                              uint8_t *out, uint16_t cap, uint16_t *out_len)
{
    if (out_len) *out_len = 0;
    if (!plugin_provides_retain_store(store)) return -1;
    return store->native_plugin->retain_load(program_md5, md5_len, out, cap, out_len);
}

int plugin_driver_retain_flush(plugin_instance_t *store)
{
    // Optional third hook. A plugin without it is assumed to commit inside
    // save(), which is where durability belongs anyway — so "nothing to do" is
    // success, not a failure to report on every stop.
    if (!store || !store->native_plugin || !store->native_plugin->retain_flush) return 0;
    return store->native_plugin->retain_flush();
}

void plugin_driver_cycle_start(plugin_driver_t *driver)
{
    if (!driver || driver->plugin_count == 0)
    {
        return;
    }

    for (int i = 0; i < driver->plugin_count; i++)
    {
        plugin_instance_t *plugin = &driver->plugins[i];

        // Skip non-running plugins
        if (!plugin->running)
        {
            continue;
        }

        // Only native plugins support cycle hooks (they can run in real-time)
        if (plugin->config.type == PLUGIN_TYPE_NATIVE && plugin->native_plugin &&
            plugin->native_plugin->cycle_start)
        {
            plugin->native_plugin->cycle_start();
        }
    }
}

// Call cycle_end for all active native plugins that have registered the hook
// This should be called at the end of each PLC scan cycle, after PLC logic execution
// Plugins opt-in by implementing cycle_end(); opt-out by not implementing it (NULL pointer)
void plugin_driver_cycle_end(plugin_driver_t *driver)
{
    if (!driver || driver->plugin_count == 0)
    {
        return;
    }

    for (int i = 0; i < driver->plugin_count; i++)
    {
        plugin_instance_t *plugin = &driver->plugins[i];

        // Skip non-running plugins
        if (!plugin->running)
        {
            continue;
        }

        // Only native plugins support cycle hooks (they can run in real-time)
        if (plugin->config.type == PLUGIN_TYPE_NATIVE && plugin->native_plugin &&
            plugin->native_plugin->cycle_end)
        {
            plugin->native_plugin->cycle_end();
        }
    }
}

// Route a command to a specific plugin by name
int plugin_driver_execute_command(plugin_driver_t *driver, const char *plugin_name,
                                  const char *command_json, char *response, size_t response_size)
{
    if (!driver || !plugin_name || !command_json)
    {
        snprintf(response, response_size, "{\"error\":\"invalid arguments\"}");
        return -1;
    }

    for (int i = 0; i < driver->plugin_count; i++)
    {
        plugin_instance_t *plugin = &driver->plugins[i];
        if (strcmp(plugin->config.name, plugin_name) != 0)
            continue;

        if (plugin->config.type == PLUGIN_TYPE_NATIVE && plugin->native_plugin &&
            plugin->native_plugin->execute_command)
        {
            return plugin->native_plugin->execute_command(command_json, response, response_size);
        }

        /* Plugin is present in the config but its symbols never loaded
         * (degraded), so it cannot service commands. Tell the caller it is
         * unavailable rather than reporting a misleading "not supported". */
        if (plugin->degraded)
        {
            snprintf(response, response_size,
                     "{\"error\":\"plugin '%s' failed to load and is unavailable. Check the "
                     "runtime logs for the cause (e.g. a missing system dependency).\"}",
                     plugin_name);
            return -1;
        }

        snprintf(response, response_size,
                 "{\"error\":\"plugin '%s' does not support execute_command\"}", plugin_name);
        return -1;
    }

    snprintf(response, response_size, "{\"error\":\"plugin '%s' not found\"}", plugin_name);
    return -1;
}

#define PLUGIN_STATS_SLOT_BUDGET   1024
#define PLUGIN_STATS_TOTAL_BUDGET  8192

size_t plugin_driver_append_stats_json(plugin_driver_t *driver, char *buffer,
                                       size_t buffer_size)
{
    if (!buffer || buffer_size == 0)
        return 0;

    size_t len = strlen(buffer);

    // Detect and strip a trailing newline — we'll re-add it after splicing.
    int had_newline = 0;
    if (len > 0 && buffer[len - 1] == '\n')
    {
        had_newline = 1;
        buffer[--len] = '\0';
    }

    // Expect the core STATS payload to end in '}'. If it doesn't, the
    // response is malformed and we won't risk making it worse.
    if (len == 0 || buffer[len - 1] != '}')
    {
        if (had_newline && len + 1 < buffer_size)
        {
            buffer[len] = '\n';
            buffer[len + 1] = '\0';
            len++;
        }
        return len;
    }

    if (!driver || driver->plugin_count == 0)
    {
        if (had_newline && len + 1 < buffer_size)
        {
            buffer[len] = '\n';
            buffer[len + 1] = '\0';
            len++;
        }
        return len;
    }

    // Build the ,"plugin_stats":{...} section in a scratch buffer so we
    // can commit-or-rollback atomically if it would overflow the response.
    char scratch[PLUGIN_STATS_TOTAL_BUDGET];
    size_t spos = 0;
    int emitted = 0;

    for (int i = 0; i < driver->plugin_count; i++)
    {
        plugin_instance_t *p = &driver->plugins[i];
        if (p->config.type != PLUGIN_TYPE_NATIVE)
            continue;
        if (!p->native_plugin || !p->native_plugin->get_stats)
            continue;

        char slot[PLUGIN_STATS_SLOT_BUDGET];
        slot[0] = '\0';
        if (p->native_plugin->get_stats(slot, sizeof(slot)) != 0)
            continue;

        size_t slen = strnlen(slot, sizeof(slot));
        if (slen < 2 || slot[0] != '{' || slot[slen - 1] != '}')
            continue; // malformed — drop silently

        int n = snprintf(scratch + spos, sizeof(scratch) - spos, "%s\"%s\":%s",
                         emitted ? "," : "", p->config.name, slot);
        if (n < 0 || (size_t)n >= sizeof(scratch) - spos)
            break; // scratch full; commit what we have

        spos += (size_t)n;
        emitted = 1;
    }

    if (!emitted)
    {
        if (had_newline && len + 1 < buffer_size)
        {
            buffer[len] = '\n';
            buffer[len + 1] = '\0';
            len++;
        }
        return len;
    }

    // Splice: overwrite the closing '}' with ,"plugin_stats":{...}} and
    // re-append the newline if present.
    size_t insert_pos = len - 1;
    int n = snprintf(buffer + insert_pos, buffer_size - insert_pos,
                     ",\"plugin_stats\":{%s}}%s", scratch, had_newline ? "\n" : "");
    if (n < 0)
    {
        // snprintf failure — restore newline and bail.
        if (had_newline && len + 1 < buffer_size)
        {
            buffer[len] = '\n';
            buffer[len + 1] = '\0';
            len++;
        }
        return len;
    }
    if ((size_t)n >= buffer_size - insert_pos)
    {
        // Would overflow the response buffer; roll back by restoring the '}'
        // and the newline.
        buffer[insert_pos] = '}';
        buffer[insert_pos + 1] = '\0';
        len = insert_pos + 1;
        if (had_newline && len + 1 < buffer_size)
        {
            buffer[len] = '\n';
            buffer[len + 1] = '\0';
            len++;
        }
        return len;
    }

    return insert_pos + (size_t)n;
}

// Cleanup Python plugin

