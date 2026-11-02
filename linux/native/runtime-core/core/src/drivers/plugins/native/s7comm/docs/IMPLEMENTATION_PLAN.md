# S7Comm Plugin Implementation Plan for OpenPLC Runtime v4

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Background and Motivation](#background-and-motivation)
3. [Analysis of Existing Implementation](#analysis-of-existing-implementation)
   - [OpenPLC v3 S7Comm Architecture](#openplc-v3-s7comm-architecture)
   - [Snap7 Library API](#snap7-library-api)
   - [OpenPLC Runtime v4 Plugin Architecture](#openplc-runtime-v4-plugin-architecture)
4. [Design Decisions](#design-decisions)
5. [JSON Configuration Schema](#json-configuration-schema)
6. [Plugin Structure](#plugin-structure)
7. [Implementation Details](#implementation-details)
8. [Phased Implementation Plan](#phased-implementation-plan)
9. [Testing Strategy](#testing-strategy)
10. [References](#references)

---

## Executive Summary

This document outlines the plan to port S7Comm functionality from OpenPLC Runtime v3 to the new v4 plugin architecture. The implementation leverages the Snap7 open-source library to provide Siemens S7 protocol server capabilities, allowing OpenPLC to communicate with S7-compatible HMIs, SCADA systems, and other industrial equipment.

Key improvements over v3:
- Full JSON-based configuration
- Flexible data block mapping to any OpenPLC buffer
- Native C/C++ plugin for optimal performance
- Thread-safe design with PLC cycle synchronization
- Configurable PLC identity for S7 client compatibility
- Comprehensive logging and diagnostics

---

## Background and Motivation

### What is S7Comm?

S7Comm (S7 Communication) is the proprietary protocol used by Siemens S7 PLCs (S7-300, S7-400, S7-1200, S7-1500). It runs over ISO-on-TCP (RFC 1006) on port 102 and is widely supported by:

- Siemens WinCC and TIA Portal
- Many third-party HMI/SCADA systems
- Industrial data acquisition tools
- OPC servers

### Why Add S7Comm to OpenPLC v4?

1. **Industry Compatibility**: Many existing installations use S7Comm-compatible equipment
2. **User Request**: Feature was present in v3 and users expect it in v4
3. **Protocol Maturity**: S7Comm is well-documented and stable
4. **Snap7 Library**: High-quality open-source implementation available

### Snap7 Library

Snap7 is an open-source, multi-platform Ethernet communication suite for interfacing with Siemens S7 PLCs. It provides:

- S7 Server (emulate a PLC)
- S7 Client (connect to PLCs)
- S7 Partner (peer-to-peer communication)

Repository: https://github.com/davenardella/snap7

---

## Analysis of Existing Implementation

### OpenPLC v3 S7Comm Architecture

The v3 implementation is located at `OpenPLC_v3/utils/snap7_src/wrapper/`:

#### Key Files

| File | Purpose |
|------|---------|
| `oplc_snap7.cpp` | Main implementation - server lifecycle, callbacks, buffer mapping |
| `oplc_snap7.h` | Header with S7 protocol constants and function declarations |

#### Data Area Mapping (Hardcoded in v3)

| S7 Area | DB Number | OpenPLC Buffer | Description |
|---------|-----------|----------------|-------------|
| PE (0x81) | N/A | `bool_input[][]` | Process inputs (digital) |
| PA (0x82) | N/A | `bool_output[][]` | Process outputs (digital) |
| MK (0x83) | N/A | Internal `MK[]` | Marker/flag memory |
| DB | 2 | `int_input[]` | Input words (%IW) |
| DB | 102 | `int_output[]` | Output words (%QW) |
| DB | 1002 | `int_memory[]` | Memory words (%MW) |
| DB | 1004 | `dint_memory[]` | Memory double-words (%MD) |

#### Server Lifecycle (v3)

```c
// Initialization
void initializeSnap7() {
    Server = new TS7Server;
    Server->SetEventsMask(0x3ff);
    Server->SetEventsCallback(EventCallBack, NULL);
    Server->SetRWAreaCallback(RWAreaCallBack, NULL);
}

// Start server
void startSnap7() {
    Server->StartTo("0.0.0.0");  // Port 102
}

// Stop server
void stopSnap7() {
    Server->Stop();
}

// Cleanup
void finalizeSnap7() {
    Server->Stop();
    delete Server;
}
```

#### RW Callback Pattern (v3)

```c
int S7API RWAreaCallBack(void* usrPtr, int Sender, int Operation,
                         PS7Tag PTag, void* pUsrData) {
    pthread_mutex_lock(&bufferLock);  // Thread safety

    switch (PTag->Area) {
        case S7AreaPE:  // Process inputs
        case S7AreaPA:  // Process outputs
            // Handle bit/byte access
            break;
        case S7AreaMK:  // Markers
            // Handle marker access
            break;
        case S7AreaDB:  // Data blocks
            switch (PTag->DBNumber) {
                case 2:     // int_input
                case 102:   // int_output
                case 1002:  // int_memory
                case 1004:  // dint_memory
            }
            break;
    }

    pthread_mutex_unlock(&bufferLock);
    return result;
}
```

#### Limitations of v3 Implementation

1. **Hardcoded DB Numbers**: Cannot change DB2, DB102, DB1002, DB1004
2. **Fixed Buffer Sizes**: Limited to BUFFER_SIZE constant
3. **No Configuration File**: All settings compiled in
4. **Tight Coupling**: Integrated directly into main runtime
5. **Limited Logging**: Minimal diagnostic output
6. **No PLC Identity Config**: Fixed SZL responses

---

### Snap7 Library API

#### Server Creation and Lifecycle

```c
// C API
S7Object Srv_Create();
void Srv_Destroy(S7Object *Server);
int Srv_Start(S7Object Server);
int Srv_StartTo(S7Object Server, const char *Address);
int Srv_Stop(S7Object Server);

// C++ API
class TS7Server {
public:
    TS7Server();
    ~TS7Server();
    int Start();
    int StartTo(const char *Address);
    int Stop();
};
```

#### Configuration Parameters

| Parameter ID | Constant | Type | Default | Description |
|--------------|----------|------|---------|-------------|
| 1 | `p_u16_LocalPort` | uint16 | 102 | TCP listening port |
| 3 | `p_i32_PingTimeout` | int32 | 10000 | Keep-alive timeout (ms) |
| 4 | `p_i32_SendTimeout` | int32 | 3000 | Socket send timeout (ms) |
| 5 | `p_i32_RecvTimeout` | int32 | 3000 | Socket receive timeout (ms) |
| 6 | `p_i32_WorkInterval` | int32 | 100 | Worker thread interval (ms) |
| 10 | `p_i32_PDURequest` | int32 | 480 | PDU size (240-960) |
| 11 | `p_i32_MaxClients` | int32 | 32 | Max concurrent connections |

```c
int Srv_GetParam(S7Object Server, int ParamNumber, void *pValue);
int Srv_SetParam(S7Object Server, int ParamNumber, void *pValue);
```

#### Data Area Registration

```c
// Area codes
const int srvAreaPE = 0;  // Process inputs (I)
const int srvAreaPA = 1;  // Process outputs (Q)
const int srvAreaMK = 2;  // Markers (M)
const int srvAreaCT = 3;  // Counters (C)
const int srvAreaTM = 4;  // Timers (T)
const int srvAreaDB = 5;  // Data blocks (DB)

// Registration functions
int Srv_RegisterArea(S7Object Server, int AreaCode, word Index,
                     void *pUsrData, int Size);
int Srv_UnregisterArea(S7Object Server, int AreaCode, word Index);

// Thread-safe access
int Srv_LockArea(S7Object Server, int AreaCode, word Index);
int Srv_UnlockArea(S7Object Server, int AreaCode, word Index);
```

#### Callback Mechanisms

```c
// Tag structure for RW operations
typedef struct {
    int Area;       // Area code (S7AreaXX)
    int DBNumber;   // DB number (for S7AreaDB)
    int Start;      // Starting byte offset
    int Size;       // Data size in bytes
    int WordLen;    // Word length code
} TS7Tag, *PS7Tag;

// Word length codes
const int S7WLBit    = 0x01;  // 1 bit
const int S7WLByte   = 0x02;  // 8 bits
const int S7WLChar   = 0x03;  // 8 bits
const int S7WLWord   = 0x04;  // 16 bits
const int S7WLDWord  = 0x06;  // 32 bits
const int S7WLInt    = 0x05;  // 16 bits signed
const int S7WLDInt   = 0x07;  // 32 bits signed
const int S7WLReal   = 0x08;  // 32 bits float

// Callback types
typedef int (S7API *pfn_RWAreaCallBack)(void *usrPtr, int Sender,
                                        int Operation, PS7Tag PTag,
                                        void *pUsrData);
typedef void (S7API *pfn_SrvCallBack)(void *usrPtr, PSrvEvent PEvent,
                                      int Size);

// Operation codes
const int OperationRead  = 0;
const int OperationWrite = 1;

// Set callbacks
int Srv_SetRWAreaCallback(S7Object Server, pfn_RWAreaCallBack pCallback,
                          void *usrPtr);
int Srv_SetEventsCallback(S7Object Server, pfn_SrvCallBack pCallback,
                          void *usrPtr);
```

#### Event Codes

```c
// Server events
const longword evcServerStarted       = 0x00000001;
const longword evcServerStopped       = 0x00000002;
const longword evcListenerCannotStart = 0x00000004;
const longword evcClientAdded         = 0x00000008;
const longword evcClientRejected      = 0x00000010;
const longword evcClientNoRoom        = 0x00000020;
const longword evcClientException     = 0x00000040;
const longword evcClientDisconnected  = 0x00000080;

// Protocol events
const longword evcPDUincoming   = 0x00010000;
const longword evcDataRead      = 0x00020000;
const longword evcDataWrite     = 0x00040000;
const longword evcNegotiatePDU  = 0x00080000;
const longword evcReadSZL       = 0x00100000;
const longword evcClock         = 0x00200000;
const longword evcUpload        = 0x00400000;
const longword evcDownload      = 0x00800000;
```

---

### OpenPLC Runtime v4 Plugin Architecture

#### Plugin Configuration File (`plugins.conf`)

```
name,path,enabled,type,config_path,venv_path
```

| Field | Description |
|-------|-------------|
| `name` | Unique plugin identifier (max 64 chars) |
| `path` | Path to `.py` or `.so` file |
| `enabled` | 1=enabled, 0=disabled |
| `type` | 0=Python, 1=Native C/C++ |
| `config_path` | Path to JSON config file |
| `venv_path` | Python venv path (Python plugins only) |

#### Plugin Lifecycle Functions

| Function | Required | Description |
|----------|----------|-------------|
| `init(void *args)` | Yes | Initialize plugin, copy runtime args |
| `start_loop()` | Yes | Start background operations |
| `stop_loop()` | Yes | Stop background operations |
| `cleanup()` | No | Release resources |
| `cycle_start()` | No | Called at PLC scan cycle start (native only) |
| `cycle_end()` | No | Called at PLC scan cycle end (native only) |

#### Runtime Arguments Structure

```c
typedef struct {
    // Boolean buffers [1024][8]
    IEC_BOOL *(*bool_input)[8];
    IEC_BOOL *(*bool_output)[8];
    IEC_BOOL *(*bool_memory)[8];

    // Byte buffers [1024]
    IEC_BYTE **byte_input;
    IEC_BYTE **byte_output;

    // Integer buffers [1024]
    IEC_UINT **int_input;
    IEC_UINT **int_output;
    IEC_UINT **int_memory;

    // Double integer buffers [1024]
    IEC_UDINT **dint_input;
    IEC_UDINT **dint_output;
    IEC_UDINT **dint_memory;

    // Long integer buffers [1024]
    IEC_ULINT **lint_input;
    IEC_ULINT **lint_output;
    IEC_ULINT **lint_memory;

    // Synchronization
    int (*mutex_take)(pthread_mutex_t *mutex);
    int (*mutex_give)(pthread_mutex_t *mutex);
    pthread_mutex_t *buffer_mutex;

    // Logging
    void (*log_info)(const char *format, ...);
    void (*log_debug)(const char *format, ...);
    void (*log_warn)(const char *format, ...);
    void (*log_error)(const char *format, ...);

    // Configuration
    const char *config_file_path;

    // Buffer dimensions
    int buffer_size;      // 1024
    int bits_per_buffer;  // 8
} plugin_runtime_args_t;
```

#### Buffer Access Pattern

```c
// Thread-safe buffer access
args->mutex_take(args->buffer_mutex);

// Read from bool_input[buffer_idx][bit_idx]
IEC_BOOL *ptr = args->bool_input[buffer_idx][bit_idx];
if (ptr != NULL) {
    value = *ptr;
}

// Write to int_output[buffer_idx]
IEC_UINT *ptr = args->int_output[buffer_idx];
if (ptr != NULL) {
    *ptr = value;
}

args->mutex_give(args->buffer_mutex);
```

---

## Design Decisions

### 1. Native C/C++ Plugin (Not Python)

**Rationale:**
- Snap7 is a C/C++ library with complex threading
- Low-latency buffer access required for industrial protocols
- No GIL contention with PLC cycle
- Direct memory operations for endianness conversion

### 2. Callback-Based Data Access with Cycle Hooks

**Approach:**
- Use Snap7 RW callback for immediate data access
- Implement `cycle_start()` and `cycle_end()` for optional buffering
- Mutex protection during callback execution

**Benefits:**
- Real-time data access for S7 clients
- Option to batch updates with PLC cycle
- Flexible synchronization strategy

### 3. Configurable Data Block Mapping

**Approach:**
- JSON configuration defines DB numbers and buffer mappings
- Each DB maps to a specific OpenPLC buffer type
- Support for custom start offsets

**Benefits:**
- Users can match existing HMI configurations
- No recompilation needed to change mappings
- Multiple configurations for different deployments

### 4. Event-Driven Logging

**Approach:**
- Configurable event mask for Snap7
- Log levels controlled via JSON config
- Integration with OpenPLC logging system

**Benefits:**
- Minimal performance impact when disabled
- Detailed diagnostics when needed
- Consistent log format with rest of runtime

---

## JSON Configuration Schema

### Complete Configuration Example

```json
{
  "server": {
    "enabled": true,
    "bind_address": "0.0.0.0",
    "port": 102,
    "max_clients": 32,
    "work_interval_ms": 100,
    "send_timeout_ms": 3000,
    "recv_timeout_ms": 3000,
    "ping_timeout_ms": 10000,
    "pdu_size": 480
  },
  "plc_identity": {
    "name": "OpenPLC Runtime",
    "module_type": "CPU 315-2 PN/DP",
    "serial_number": "S C-XXXXXXXXX",
    "copyright": "OpenPLC Project",
    "module_name": "OpenPLC"
  },
  "data_blocks": [
    {
      "db_number": 1,
      "description": "Digital Inputs (%IX)",
      "size_bytes": 128,
      "mapping": {
        "type": "bool_input",
        "start_buffer": 0,
        "bit_addressing": true
      }
    },
    {
      "db_number": 2,
      "description": "Digital Outputs (%QX)",
      "size_bytes": 128,
      "mapping": {
        "type": "bool_output",
        "start_buffer": 0,
        "bit_addressing": true
      }
    },
    {
      "db_number": 10,
      "description": "Analog Inputs (%IW)",
      "size_bytes": 2048,
      "mapping": {
        "type": "int_input",
        "start_buffer": 0,
        "bit_addressing": false
      }
    },
    {
      "db_number": 20,
      "description": "Analog Outputs (%QW)",
      "size_bytes": 2048,
      "mapping": {
        "type": "int_output",
        "start_buffer": 0,
        "bit_addressing": false
      }
    },
    {
      "db_number": 100,
      "description": "Memory Words (%MW)",
      "size_bytes": 2048,
      "mapping": {
        "type": "int_memory",
        "start_buffer": 0,
        "bit_addressing": false
      }
    },
    {
      "db_number": 200,
      "description": "Memory DWords (%MD)",
      "size_bytes": 4096,
      "mapping": {
        "type": "dint_memory",
        "start_buffer": 0,
        "bit_addressing": false
      }
    }
  ],
  "system_areas": {
    "pe_area": {
      "enabled": true,
      "size_bytes": 128,
      "mapping": {
        "type": "bool_input",
        "start_buffer": 0
      }
    },
    "pa_area": {
      "enabled": true,
      "size_bytes": 128,
      "mapping": {
        "type": "bool_output",
        "start_buffer": 0
      }
    },
    "mk_area": {
      "enabled": true,
      "size_bytes": 256,
      "mapping": {
        "type": "bool_memory",
        "start_buffer": 0
      }
    }
  },
  "logging": {
    "log_connections": true,
    "log_data_access": false,
    "log_errors": true
  }
}
```

### Configuration Field Reference

#### Server Section

| Field | Type | Default | Range | Description |
|-------|------|---------|-------|-------------|
| `enabled` | bool | true | - | Enable/disable the S7 server |
| `bind_address` | string | "0.0.0.0" | Valid IP | Network interface to bind |
| `port` | int | 102 | 1-65535 | S7Comm TCP port |
| `max_clients` | int | 32 | 1-1024 | Maximum simultaneous connections |
| `work_interval_ms` | int | 100 | 1-10000 | Worker thread polling interval |
| `send_timeout_ms` | int | 3000 | 100-60000 | Socket send timeout |
| `recv_timeout_ms` | int | 3000 | 100-60000 | Socket receive timeout |
| `ping_timeout_ms` | int | 10000 | 1000-300000 | Keep-alive timeout |
| `pdu_size` | int | 480 | 240-960 | Maximum PDU size |

#### PLC Identity Section

These values are returned in S7 SZL (System State List) queries:

| Field | S7 SZL | Description |
|-------|--------|-------------|
| `name` | 0x001C Index 1 | PLC name (max 32 chars) |
| `module_type` | 0x001C Index 2 | CPU type string |
| `serial_number` | 0x001C Index 3 | Serial number |
| `copyright` | 0x001C Index 4 | Copyright string |
| `module_name` | 0x001C Index 5 | Module name |

#### Data Blocks Section

| Field | Type | Description |
|-------|------|-------------|
| `db_number` | int | S7 DB number (1-65535) |
| `description` | string | Human-readable description |
| `size_bytes` | int | DB size in bytes |
| `mapping.type` | enum | OpenPLC buffer type (see below) |
| `mapping.start_buffer` | int | Starting buffer index |
| `mapping.bit_addressing` | bool | Enable bit-level access |

#### Supported Buffer Types

| Type | IEC Type | Element Size | Max Elements |
|------|----------|--------------|--------------|
| `bool_input` | BOOL | 1 bit | 8192 (1024 * 8) |
| `bool_output` | BOOL | 1 bit | 8192 |
| `bool_memory` | BOOL | 1 bit | 8192 |
| `byte_input` | BYTE | 1 byte | 1024 |
| `byte_output` | BYTE | 1 byte | 1024 |
| `int_input` | UINT/INT | 2 bytes | 1024 |
| `int_output` | UINT/INT | 2 bytes | 1024 |
| `int_memory` | UINT/INT | 2 bytes | 1024 |
| `dint_input` | UDINT/DINT | 4 bytes | 1024 |
| `dint_output` | UDINT/DINT | 4 bytes | 1024 |
| `dint_memory` | UDINT/DINT | 4 bytes | 1024 |
| `lint_input` | ULINT/LINT | 8 bytes | 1024 |
| `lint_output` | ULINT/LINT | 8 bytes | 1024 |
| `lint_memory` | ULINT/LINT | 8 bytes | 1024 |

#### System Areas Section

| Area | S7 Code | Description |
|------|---------|-------------|
| `pe_area` | 0x81 | Process inputs (I area) |
| `pa_area` | 0x82 | Process outputs (Q area) |
| `mk_area` | 0x83 | Markers (M area) |

#### Logging Section

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `log_connections` | bool | true | Log client connect/disconnect |
| `log_data_access` | bool | false | Log read/write operations |
| `log_errors` | bool | true | Log errors and warnings |

---

## Plugin Structure

### Directory Layout

```
core/src/drivers/plugins/native/s7comm/
├── CMakeLists.txt                 # Build configuration
├── s7comm_plugin.c                # Main plugin implementation
├── s7comm_plugin.h                # Plugin header
├── s7comm_config.c                # JSON configuration parser
├── s7comm_config.h                # Configuration structures
├── s7comm_buffer_mapping.c        # OpenPLC buffer mapping logic
├── s7comm_buffer_mapping.h        # Buffer mapping header
├── s7comm_callbacks.c             # Snap7 RW and event callbacks
├── s7comm_callbacks.h             # Callback function declarations
├── s7comm_config.json             # Default configuration file
├── docs/
│   ├── IMPLEMENTATION_PLAN.md     # This document
│   └── USER_GUIDE.md              # User documentation
├── snap7/                         # Snap7 library
│   ├── snap7.h                    # Snap7 header
│   ├── snap7.cpp                  # Snap7 C++ wrapper
│   └── src/                       # Snap7 source files
│       ├── core/
│       │   ├── s7_server.h
│       │   ├── s7_server.cpp
│       │   ├── s7_types.h
│       │   └── s7_isotcp.h/cpp
│       └── sys/
│           ├── snap_tcpsrvr.h/cpp
│           └── snap_threads.h
└── tests/
    ├── test_config.c              # Configuration parsing tests
    ├── test_buffer_mapping.c      # Buffer mapping tests
    └── test_integration.py        # Integration tests with python-snap7
```

### Core Data Structures

```c
// s7comm_config.h

#ifndef S7COMM_CONFIG_H
#define S7COMM_CONFIG_H

#include <stdint.h>
#include <stdbool.h>
#include <pthread.h>

#define MAX_DATA_BLOCKS 64
#define MAX_STRING_LEN 64
#define MAX_DESCRIPTION_LEN 128

// Buffer type enumeration
typedef enum {
    BUFFER_TYPE_NONE = 0,
    BUFFER_TYPE_BOOL_INPUT,
    BUFFER_TYPE_BOOL_OUTPUT,
    BUFFER_TYPE_BOOL_MEMORY,
    BUFFER_TYPE_BYTE_INPUT,
    BUFFER_TYPE_BYTE_OUTPUT,
    BUFFER_TYPE_INT_INPUT,
    BUFFER_TYPE_INT_OUTPUT,
    BUFFER_TYPE_INT_MEMORY,
    BUFFER_TYPE_DINT_INPUT,
    BUFFER_TYPE_DINT_OUTPUT,
    BUFFER_TYPE_DINT_MEMORY,
    BUFFER_TYPE_LINT_INPUT,
    BUFFER_TYPE_LINT_OUTPUT,
    BUFFER_TYPE_LINT_MEMORY
} buffer_type_t;

// Buffer mapping configuration
typedef struct {
    buffer_type_t type;
    int start_buffer;
    bool bit_addressing;
} buffer_mapping_t;

// Data block configuration
typedef struct {
    int db_number;
    char description[MAX_DESCRIPTION_LEN];
    int size_bytes;
    buffer_mapping_t mapping;
    uint8_t *data_buffer;      // Allocated S7 data buffer
    pthread_mutex_t *lock;     // Per-DB lock for thread safety
} data_block_config_t;

// System area configuration
typedef struct {
    bool enabled;
    int size_bytes;
    buffer_mapping_t mapping;
    uint8_t *data_buffer;
} system_area_config_t;

// PLC identity configuration
typedef struct {
    char name[MAX_STRING_LEN];
    char module_type[MAX_STRING_LEN];
    char serial_number[MAX_STRING_LEN];
    char copyright[MAX_STRING_LEN];
    char module_name[MAX_STRING_LEN];
} plc_identity_t;

// Logging configuration
typedef struct {
    bool log_connections;
    bool log_data_access;
    bool log_errors;
} logging_config_t;

// Complete S7Comm configuration
typedef struct {
    // Server settings
    bool enabled;
    char bind_address[MAX_STRING_LEN];
    uint16_t port;
    int max_clients;
    int work_interval_ms;
    int send_timeout_ms;
    int recv_timeout_ms;
    int ping_timeout_ms;
    int pdu_size;

    // PLC identity
    plc_identity_t identity;

    // Data blocks
    int num_data_blocks;
    data_block_config_t data_blocks[MAX_DATA_BLOCKS];

    // System areas
    system_area_config_t pe_area;
    system_area_config_t pa_area;
    system_area_config_t mk_area;

    // Logging
    logging_config_t logging;
} s7comm_config_t;

// Configuration functions
int s7comm_parse_config(const char *config_path, s7comm_config_t *config);
int s7comm_validate_config(const s7comm_config_t *config);
void s7comm_free_config(s7comm_config_t *config);
void s7comm_print_config(const s7comm_config_t *config);

#endif // S7COMM_CONFIG_H
```

### Plugin Interface

```c
// s7comm_plugin.h

#ifndef S7COMM_PLUGIN_H
#define S7COMM_PLUGIN_H

#include "plugin_types.h"

// Required plugin lifecycle functions
int init(void *args);
void start_loop(void);
void stop_loop(void);
void cleanup(void);

// Optional cycle hooks (native plugins only)
void cycle_start(void);
void cycle_end(void);

#endif // S7COMM_PLUGIN_H
```

---

## Implementation Details

### Endianness Handling

S7Comm uses big-endian (network byte order). OpenPLC uses native endianness.

```c
// s7comm_buffer_mapping.c

#include <endian.h>

// Convert host to S7 (big-endian)
static inline uint16_t host_to_s7_16(uint16_t val) {
    return htobe16(val);
}

static inline uint32_t host_to_s7_32(uint32_t val) {
    return htobe32(val);
}

static inline uint64_t host_to_s7_64(uint64_t val) {
    return htobe64(val);
}

// Convert S7 to host
static inline uint16_t s7_to_host_16(uint16_t val) {
    return be16toh(val);
}

static inline uint32_t s7_to_host_32(uint32_t val) {
    return be32toh(val);
}

static inline uint64_t s7_to_host_64(uint64_t val) {
    return be64toh(val);
}
```

### Bit Addressing

S7 uses byte.bit addressing (e.g., DBX10.3 = byte 10, bit 3):

```c
// Convert S7 bit address to OpenPLC buffer indices
static void s7_bit_to_openplc(int s7_bit_address, int start_buffer,
                              int *buffer_idx, int *bit_idx) {
    int s7_byte = s7_bit_address / 8;
    int s7_bit = s7_bit_address % 8;

    *buffer_idx = start_buffer + s7_byte;
    *bit_idx = s7_bit;
}

// Read single bit from OpenPLC buffer
static int read_bool_bit(plugin_runtime_args_t *args, buffer_type_t type,
                         int buffer_idx, int bit_idx, uint8_t *value) {
    IEC_BOOL *(*buffer)[8] = NULL;

    switch (type) {
        case BUFFER_TYPE_BOOL_INPUT:
            buffer = args->bool_input;
            break;
        case BUFFER_TYPE_BOOL_OUTPUT:
            buffer = args->bool_output;
            break;
        case BUFFER_TYPE_BOOL_MEMORY:
            buffer = args->bool_memory;
            break;
        default:
            return -1;
    }

    if (buffer_idx >= args->buffer_size || bit_idx >= args->bits_per_buffer) {
        return -1;
    }

    IEC_BOOL *ptr = buffer[buffer_idx][bit_idx];
    *value = (ptr != NULL) ? *ptr : 0;
    return 0;
}
```

### Thread Safety Strategy

```c
// s7comm_callbacks.c

int S7API s7comm_rw_callback(void *usrPtr, int Sender, int Operation,
                             PS7Tag PTag, void *pUsrData) {
    s7comm_context_t *ctx = (s7comm_context_t *)usrPtr;
    plugin_runtime_args_t *args = ctx->runtime_args;

    // Acquire OpenPLC buffer mutex
    args->mutex_take(args->buffer_mutex);

    int result = 0;
    bool is_read = (Operation == OperationRead);

    switch (PTag->Area) {
        case S7AreaPE:
            result = handle_pe_access(ctx, PTag, pUsrData, is_read);
            break;
        case S7AreaPA:
            result = handle_pa_access(ctx, PTag, pUsrData, is_read);
            break;
        case S7AreaMK:
            result = handle_mk_access(ctx, PTag, pUsrData, is_read);
            break;
        case S7AreaDB:
            result = handle_db_access(ctx, PTag, pUsrData, is_read);
            break;
        default:
            result = errSrvUnknownArea;
            break;
    }

    // Release mutex
    args->mutex_give(args->buffer_mutex);

    return result;
}
```

### DB Access Handler

```c
static int handle_db_access(s7comm_context_t *ctx, PS7Tag PTag,
                           void *pUsrData, bool is_read) {
    // Find matching DB configuration
    data_block_config_t *db = find_db_config(ctx->config, PTag->DBNumber);
    if (db == NULL) {
        return errSrvUnknownArea;
    }

    // Validate access bounds
    if (PTag->Start + PTag->Size > db->size_bytes) {
        return errSrvInvalidParams;
    }

    // Handle based on word length
    switch (PTag->WordLen) {
        case S7WLBit:
            return handle_bit_access(ctx, db, PTag, pUsrData, is_read);
        case S7WLByte:
        case S7WLChar:
            return handle_byte_access(ctx, db, PTag, pUsrData, is_read);
        case S7WLWord:
        case S7WLInt:
            return handle_word_access(ctx, db, PTag, pUsrData, is_read);
        case S7WLDWord:
        case S7WLDInt:
        case S7WLReal:
            return handle_dword_access(ctx, db, PTag, pUsrData, is_read);
        default:
            return errSrvInvalidParams;
    }
}
```

### Configuration Validation

```c
// s7comm_config.c

int s7comm_validate_config(const s7comm_config_t *config) {
    // Validate port
    if (config->port == 0) {
        return -1;
    }

    // Validate timeouts
    if (config->send_timeout_ms < 100 || config->recv_timeout_ms < 100) {
        return -1;
    }

    // Validate PDU size
    if (config->pdu_size < 240 || config->pdu_size > 960) {
        return -1;
    }

    // Check for duplicate DB numbers
    for (int i = 0; i < config->num_data_blocks; i++) {
        for (int j = i + 1; j < config->num_data_blocks; j++) {
            if (config->data_blocks[i].db_number ==
                config->data_blocks[j].db_number) {
                return -1;
            }
        }
    }

    // Validate buffer mappings don't exceed limits
    for (int i = 0; i < config->num_data_blocks; i++) {
        const data_block_config_t *db = &config->data_blocks[i];
        int max_offset = get_buffer_max_offset(db->mapping.type);

        int required = db->mapping.start_buffer +
                       (db->size_bytes / get_element_size(db->mapping.type));

        if (required > max_offset) {
            return -1;
        }
    }

    return 0;
}
```

---

## Phased Implementation Plan

### Phase 1: Foundation (1-2 weeks)

**Objectives:**
- Project structure setup
- Snap7 library integration
- Basic server lifecycle

**Tasks:**
1. Create plugin directory structure
2. Copy/integrate Snap7 source files
3. Create CMakeLists.txt
4. Implement plugin lifecycle stubs (`init`, `start_loop`, `stop_loop`, `cleanup`)
5. Hardcode minimal configuration for testing
6. Test server starts and accepts connections
7. Add entry to `plugins.conf`

**Deliverables:**
- Compilable `libs7comm.so` plugin
- Server accepts connections on port 102
- Basic logging output

**Verification:**
```bash
# Build plugin
cd build && cmake .. && make s7comm_plugin

# Test with python-snap7
python3 -c "
import snap7
client = snap7.client.Client()
client.connect('127.0.0.1', 0, 0)
print('Connected:', client.get_connected())
client.disconnect()
"
```

### Phase 2: Configuration System (1 week)

**Objectives:**
- JSON configuration parsing
- All parameters configurable

**Tasks:**
1. Add cJSON library (or similar) for JSON parsing
2. Implement `s7comm_parse_config()` function
3. Create default `s7comm_config.json`
4. Implement configuration validation
5. Apply server parameters from config
6. Add PLC identity configuration

**Deliverables:**
- Plugin fully configurable via JSON
- Configuration validation with clear error messages
- Default configuration file

**Verification:**
- Modify port in config, verify server binds to new port
- Invalid config rejected with clear error message

### Phase 3: Buffer Mapping (1-2 weeks)

**Objectives:**
- Dynamic buffer allocation
- Complete S7 area mapping

**Tasks:**
1. Implement buffer allocation based on config
2. Register S7 areas (PE, PA, MK, DBs) with Snap7
3. Implement RW callback handler
4. Implement PE/PA area handlers (bool_input/bool_output)
5. Implement MK area handler (bool_memory)
6. Implement DB handlers for all buffer types
7. Add endianness conversion
8. Support bit-level addressing

**Deliverables:**
- All configured areas accessible via S7
- Correct data mapping to OpenPLC buffers
- Proper byte order handling

**Verification:**
```python
# Test DB read/write
import snap7
client = snap7.client.Client()
client.connect('127.0.0.1', 0, 0)

# Write to DB10 (int_input mapping)
data = bytearray([0x12, 0x34])
client.db_write(10, 0, data)

# Read back
result = client.db_read(10, 0, 2)
assert result == data
```

### Phase 4: Thread Safety (1 week)

**Objectives:**
- Ensure thread-safe buffer access
- Implement cycle hooks

**Tasks:**
1. Integrate with plugin mutex (buffer_mutex)
2. Test concurrent access from multiple clients
3. Implement `cycle_start()` hook
4. Implement `cycle_end()` hook
5. Add stress testing
6. Profile and optimize hot paths

**Deliverables:**
- No race conditions under load
- Deterministic buffer updates
- Performance metrics

**Verification:**
- Run multiple S7 clients simultaneously
- Verify PLC cycle timing unaffected
- Memory sanitizer clean run

### Phase 5: Logging and Diagnostics (0.5 weeks)

**Objectives:**
- Comprehensive logging
- Event handling

**Tasks:**
1. Implement Snap7 event callback
2. Add connection logging (configurable)
3. Add data access logging (configurable)
4. Add error logging with context
5. Implement server status reporting

**Deliverables:**
- Configurable logging output
- Clear diagnostic messages
- Status queryable via logs

### Phase 6: Testing and Documentation (1 week)

**Objectives:**
- Comprehensive test coverage
- User documentation

**Tasks:**
1. Unit tests for configuration parsing
2. Unit tests for buffer mapping
3. Integration tests with python-snap7
4. Performance benchmarks
5. Write USER_GUIDE.md
6. Add configuration examples
7. Update main plugin README

**Deliverables:**
- Test suite with >80% coverage
- Complete user documentation
- Example configurations

### Phase 7: Advanced Features (Optional, Future)

**Potential enhancements:**
1. SZL data responses (system queries)
2. Password protection for areas
3. Block upload/download simulation
4. Counter/Timer area support
5. WebSocket monitoring interface
6. Multiple server instances
7. Connection rate limiting

---

## Testing Strategy

### Unit Tests

```c
// test_config.c

void test_parse_valid_config(void) {
    s7comm_config_t config;
    int result = s7comm_parse_config("test_config.json", &config);

    assert(result == 0);
    assert(config.port == 102);
    assert(config.num_data_blocks == 6);
    assert(config.data_blocks[0].db_number == 1);
}

void test_parse_invalid_config(void) {
    s7comm_config_t config;
    int result = s7comm_parse_config("invalid_config.json", &config);

    assert(result != 0);
}

void test_validate_duplicate_db(void) {
    s7comm_config_t config = {0};
    config.num_data_blocks = 2;
    config.data_blocks[0].db_number = 1;
    config.data_blocks[1].db_number = 1;  // Duplicate

    int result = s7comm_validate_config(&config);
    assert(result != 0);
}
```

### Integration Tests

```python
# test_integration.py

import snap7
import pytest

@pytest.fixture
def s7_client():
    client = snap7.client.Client()
    client.connect('127.0.0.1', 0, 0, 102)
    yield client
    client.disconnect()

def test_read_db(s7_client):
    """Test reading from configured DB"""
    data = s7_client.db_read(10, 0, 10)
    assert len(data) == 10

def test_write_db(s7_client):
    """Test writing to configured DB"""
    data = bytearray([0x01, 0x02, 0x03, 0x04])
    s7_client.db_write(20, 0, data)
    result = s7_client.db_read(20, 0, 4)
    assert result == data

def test_read_inputs(s7_client):
    """Test reading process inputs (PE area)"""
    data = s7_client.read_area(snap7.types.Areas.PE, 0, 0, 10)
    assert len(data) == 10

def test_write_outputs(s7_client):
    """Test writing process outputs (PA area)"""
    data = bytearray([0xFF, 0x00])
    s7_client.write_area(snap7.types.Areas.PA, 0, 0, data)

def test_concurrent_access(s7_client):
    """Test multiple concurrent clients"""
    import threading

    def client_task():
        c = snap7.client.Client()
        c.connect('127.0.0.1', 0, 0, 102)
        for _ in range(100):
            c.db_read(10, 0, 10)
        c.disconnect()

    threads = [threading.Thread(target=client_task) for _ in range(10)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
```

### Performance Benchmarks

```python
# benchmark.py

import snap7
import time

def benchmark_read(client, iterations=10000):
    start = time.time()
    for _ in range(iterations):
        client.db_read(10, 0, 100)
    elapsed = time.time() - start
    print(f"Read: {iterations/elapsed:.0f} ops/sec")

def benchmark_write(client, iterations=10000):
    data = bytearray(100)
    start = time.time()
    for _ in range(iterations):
        client.db_write(20, 0, data)
    elapsed = time.time() - start
    print(f"Write: {iterations/elapsed:.0f} ops/sec")
```

---

## References

### External Documentation

1. **Snap7 Project**: https://github.com/davenardella/snap7
2. **Snap7 Documentation**: http://snap7.sourceforge.net/
3. **S7Comm Protocol Analysis**: https://github.com/orange-cyberdefense/S7Comm-Wireshark-Dissector
4. **RFC 1006 (ISO-on-TCP)**: https://tools.ietf.org/html/rfc1006

### Internal Documentation

1. **OpenPLC v4 Plugin Guide**: `core/src/drivers/README.md`
2. **Plugin Types Header**: `core/src/drivers/plugin_types.h`
3. **Image Tables**: `core/src/plc_app/image_tables.h`
4. **Architecture Overview**: `docs/ARCHITECTURE.md`

### Related Code

1. **OpenPLC v3 S7Comm**: `OpenPLC_v3/utils/snap7_src/wrapper/oplc_snap7.cpp`
2. **Modbus Slave Plugin**: `core/src/drivers/plugins/python/modbus_slave/simple_modbus.py`
3. **Native Plugin Example**: `core/src/drivers/plugins/native/examples/test_plugin.c`

---

## Appendix A: S7 Address Reference

### S7 Area Codes

| Area | Code | Description | Access |
|------|------|-------------|--------|
| PE | 0x81 | Process inputs | Read |
| PA | 0x82 | Process outputs | Read/Write |
| MK | 0x83 | Markers/Flags | Read/Write |
| DB | 0x84 | Data blocks | Read/Write |
| CT | 0x1C | Counters | Read/Write |
| TM | 0x1D | Timers | Read/Write |

### S7 Word Length Codes

| Code | Name | Size | Description |
|------|------|------|-------------|
| 0x01 | Bit | 1 bit | Single bit |
| 0x02 | Byte | 1 byte | 8 bits |
| 0x03 | Char | 1 byte | ASCII character |
| 0x04 | Word | 2 bytes | 16-bit unsigned |
| 0x05 | Int | 2 bytes | 16-bit signed |
| 0x06 | DWord | 4 bytes | 32-bit unsigned |
| 0x07 | DInt | 4 bytes | 32-bit signed |
| 0x08 | Real | 4 bytes | IEEE 754 float |

### S7 Address Examples

| S7 Address | Area | DB | Offset | Bit | Description |
|------------|------|-----|--------|-----|-------------|
| I0.0 | PE | - | 0 | 0 | Input byte 0, bit 0 |
| Q10.5 | PA | - | 10 | 5 | Output byte 10, bit 5 |
| M100.0 | MK | - | 100 | 0 | Marker byte 100, bit 0 |
| DB1.DBX0.0 | DB | 1 | 0 | 0 | DB1, byte 0, bit 0 |
| DB10.DBW0 | DB | 10 | 0 | - | DB10, word at byte 0 |
| DB10.DBD4 | DB | 10 | 4 | - | DB10, dword at byte 4 |

---

## Appendix B: Error Codes

### Snap7 Server Errors

| Code | Name | Description |
|------|------|-------------|
| 0x00100000 | errSrvCannotStart | Server failed to start |
| 0x00200000 | errSrvDBNullPointer | NULL pointer for DB registration |
| 0x00300000 | errSrvAreaAlreadyExists | Area already registered |
| 0x00400000 | errSrvUnknownArea | Unknown area requested |
| 0x00500000 | errSrvInvalidParams | Invalid parameters |
| 0x00600000 | errSrvTooManyDB | Too many DBs registered |
| 0x00700000 | errSrvInvalidParamNumber | Invalid parameter number |
| 0x00800000 | errSrvCannotChangeParam | Cannot change parameter |

---

*Document Version: 1.0*
*Last Updated: 2026-01-12*
*Author: OpenPLC Development Team*
