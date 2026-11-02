// SPDX-License-Identifier: MIT
#include "drivers/plugin_driver.h"
#include <assert.h>
#include <stdlib.h>
#include <string.h>

// This test links the real native driver. Runtime-only sections are discarded;
// these log sinks are test fixtures, not replacement production runtime APIs.
void log_info(const char *format, ...) { (void)format; }
void log_debug(const char *format, ...) { (void)format; }
void log_warn(const char *format, ...) { (void)format; }
void log_error(const char *format, ...) { (void)format; }
static int starts, stops, cycles, cleanups, saves, flushes;
static int start(void) { ++starts; return 0; }
static void stop(void) { ++stops; }
static void cycle(void) { ++cycles; }
static void cleanup(void) { ++cleanups; }
static int save(const uint8_t *blob, uint16_t size) { assert(size == 1 && *blob == 42); ++saves; return 0; }
static int load(const char *md5, uint16_t length, uint8_t *out, uint16_t cap, uint16_t *length_out) {
    assert(length == 2 && memcmp(md5, "id", 2) == 0 && cap == 1); *out = 42; *length_out = 1; return 0;
}
static int flush(void) { ++flushes; return 0; }
static int command(const char *request, char *response, size_t size) {
    assert(strcmp(request, "query") == 0 && size > 2); strcpy(response, "ok"); return 0;
}
static int stats(char *out, size_t size) { assert(size > 9); strcpy(out, "{\"ok\":1}"); return 0; }
int main(void) {
    plugin_config_t configs[2]; memset(configs, 0, sizeof(configs));
    configs[0].type = PLUGIN_TYPE_NATIVE; configs[0].enabled = 1;
    configs[1].type = PLUGIN_TYPE_PYTHON; configs[1].enabled = 0;
    assert(plugin_driver_native_config_supported(configs, 2) == 0);
    configs[1].enabled = 1;
    assert(plugin_driver_native_config_supported(configs, 2) != 0);
    configs[1].type = 9; configs[1].enabled = 0;
    assert(plugin_driver_native_config_supported(configs, 2) != 0);
    assert(plugin_driver_native_config_supported(NULL, 1) != 0);
    plugin_driver_t *driver = plugin_driver_create(); assert(driver);
    driver->plugin_count = 1;
    plugin_instance_t *p = &driver->plugins[0];
    p->config.type = PLUGIN_TYPE_NATIVE; p->config.enabled = 1; strcpy(p->config.name, "native"); p->initialized = 1;
    p->native_plugin = calloc(1, sizeof(*p->native_plugin)); assert(p->native_plugin);
    *p->native_plugin = (plugin_funct_bundle_t){ .start=start, .stop=stop, .cycle_start=cycle,
        .cycle_end=cycle, .cleanup=cleanup, .retain_save=save, .retain_load=load, .retain_flush=flush,
        .execute_command=command, .get_stats=stats };
    assert(plugin_driver_start(driver) == 0 && starts == 1 && p->running);
    plugin_driver_cycle_start(driver); plugin_driver_cycle_end(driver); assert(cycles == 2);
    assert(plugin_driver_find_retain_store(driver) == p);
    uint8_t value=42, out=0; uint16_t length=0;
    assert(plugin_driver_retain_save(p, &value, 1) == 0 && saves == 1);
    assert(plugin_driver_retain_load(p, "id", 2, &out, 1, &length) == 0 && out == 42 && length == 1);
    assert(plugin_driver_retain_flush(p) == 0 && flushes == 1);
    char response[100]; assert(plugin_driver_execute_command(driver, "native", "query", response, sizeof(response)) == 0 && strcmp(response,"ok") == 0);
    strcpy(response,"STATS:{}\n"); plugin_driver_append_stats_json(driver,response,sizeof(response));
    assert(strstr(response, "\"plugin_stats\":{\"native\":{\"ok\":1}}") && response[strlen(response)-1] == '\n');
    assert(plugin_driver_stop(driver) == 0 && stops == 1 && !p->running);
    plugin_driver_cycle_start(driver); assert(cycles == 2);
    assert(plugin_driver_cleanup_init(driver) == 1 && cleanups == 1);
    assert(plugin_driver_cleanup_init(driver) == 0 && cleanups == 1);
    plugin_driver_destroy(driver); assert(cleanups == 1 && stops == 1);
    return 0;
}
