# S7Comm Plugin JSON Configuration Schema

This document defines the JSON configuration schema for the S7Comm plugin.
It is intended for OpenPLC Editor developers implementing the configuration UI.

## JSON Structure Overview

```json
{
  "server": { ... },
  "plc_identity": { ... },
  "data_blocks": [ ... ],
  "system_areas": { ... },
  "logging": { ... }
}
```

All sections are optional. Missing sections use default values.

## Complete Schema Reference

### Root Object

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `server` | object | No | See below | Server configuration |
| `plc_identity` | object | No | See below | PLC identity for S7 clients |
| `data_blocks` | array | No | `[]` | Data block mappings |
| `system_areas` | object | No | All disabled | S7 system area mappings |
| `logging` | object | No | See below | Logging configuration |

---

### server Object

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
  }
}
```

| Field | Type | Required | Default | Constraints | Description |
|-------|------|----------|---------|-------------|-------------|
| `enabled` | boolean | No | `true` | - | Enable/disable the S7 server |
| `bind_address` | string | No | `"0.0.0.0"` | Valid IP or `"0.0.0.0"` | Network interface to bind |
| `port` | integer | No | `102` | 1-65535 | TCP port (102 = standard S7) |
| `max_clients` | integer | No | `32` | 1-128 | Max simultaneous connections |
| `work_interval_ms` | integer | No | `100` | 10-1000 | Internal polling interval (ms) |
| `send_timeout_ms` | integer | No | `3000` | 100-30000 | Socket send timeout (ms) |
| `recv_timeout_ms` | integer | No | `3000` | 100-30000 | Socket receive timeout (ms) |
| `ping_timeout_ms` | integer | No | `10000` | 1000-60000 | Keep-alive timeout (ms) |
| `pdu_size` | integer | No | `480` | 240-960 | Maximum PDU size |

**Notes:**
- Port 102 requires root/administrator privileges
- Use port > 1024 to avoid privilege requirements

---

### plc_identity Object

```json
{
  "plc_identity": {
    "name": "OpenPLC Runtime",
    "module_type": "CPU 315-2 PN/DP",
    "serial_number": "S C-XXXXXXXXX",
    "copyright": "OpenPLC Project",
    "module_name": "OpenPLC"
  }
}
```

| Field | Type | Required | Default | Constraints | Description |
|-------|------|----------|---------|-------------|-------------|
| `name` | string | No | `"OpenPLC Runtime"` | Max 64 chars | PLC name |
| `module_type` | string | No | `"CPU 315-2 PN/DP"` | Max 64 chars | CPU type string |
| `serial_number` | string | No | `"S C-XXXXXXXXX"` | Max 64 chars | Serial number |
| `copyright` | string | No | `"OpenPLC Project"` | Max 64 chars | Copyright string |
| `module_name` | string | No | `"OpenPLC"` | Max 64 chars | Module name |

**Notes:**
- These values are returned in S7 SZL (System Status List) queries
- `module_type` should match a real Siemens CPU for best HMI compatibility

---

### data_blocks Array

```json
{
  "data_blocks": [
    {
      "db_number": 1,
      "description": "Digital Inputs",
      "size_bytes": 128,
      "mapping": {
        "type": "bool_input",
        "start_buffer": 0,
        "bit_addressing": true
      }
    }
  ]
}
```

Each data block object:

| Field | Type | Required | Default | Constraints | Description |
|-------|------|----------|---------|-------------|-------------|
| `db_number` | integer | **Yes** | - | 1-65535 | S7 DB number |
| `description` | string | No | `""` | Max 128 chars | Human-readable description |
| `size_bytes` | integer | **Yes** | - | 1-65536 | DB size in bytes |
| `mapping` | object | **Yes** | - | See below | Buffer mapping configuration |

**mapping Object:**

| Field | Type | Required | Default | Constraints | Description |
|-------|------|----------|---------|-------------|-------------|
| `type` | string | **Yes** | - | See valid types | OpenPLC buffer type |
| `start_buffer` | integer | No | `0` | 0-1023 | Starting index in OpenPLC buffer |
| `bit_addressing` | boolean | No | `false` | - | Enable bit-level access (BOOL types) |

**Valid `type` values:**

| Type | IEC Address | Element Size | Direction | Description |
|------|-------------|--------------|-----------|-------------|
| `"bool_input"` | %IX | 1 bit | Read | Digital inputs |
| `"bool_output"` | %QX | 1 bit | Read/Write | Digital outputs |
| `"bool_memory"` | %MX | 1 bit | Read/Write | Internal markers |
| `"byte_input"` | %IB | 1 byte | Read | Byte inputs |
| `"byte_output"` | %QB | 1 byte | Read/Write | Byte outputs |
| `"int_input"` | %IW | 2 bytes | Read | Word inputs (UINT) |
| `"int_output"` | %QW | 2 bytes | Read/Write | Word outputs (UINT) |
| `"int_memory"` | %MW | 2 bytes | Read/Write | Word memory (UINT) |
| `"dint_input"` | %ID | 4 bytes | Read | Double word inputs (UDINT) |
| `"dint_output"` | %QD | 4 bytes | Read/Write | Double word outputs (UDINT) |
| `"dint_memory"` | %MD | 4 bytes | Read/Write | Double word memory (UDINT) |
| `"lint_input"` | %IL | 8 bytes | Read | Long word inputs (ULINT) |
| `"lint_output"` | %QL | 8 bytes | Read/Write | Long word outputs (ULINT) |
| `"lint_memory"` | %ML | 8 bytes | Read/Write | Long word memory (ULINT) |

**Notes:**
- Maximum 64 data blocks supported
- Input types are read-only from S7 client perspective
- Output and memory types are read/write

---

### system_areas Object

```json
{
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
  }
}
```

Each system area (`pe_area`, `pa_area`, `mk_area`):

| Field | Type | Required | Default | Constraints | Description |
|-------|------|----------|---------|-------------|-------------|
| `enabled` | boolean | No | `false` | - | Enable this area |
| `size_bytes` | integer | No | `0` | 1-65536 | Area size in bytes |
| `mapping` | object | No | - | See above | Buffer mapping (same as data_blocks) |

**Area Descriptions:**

| Area | S7 Area Code | S7 Address | Typical Use |
|------|--------------|------------|-------------|
| `pe_area` | PE (Process Inputs) | I (e.g., I0.0) | Digital/analog inputs |
| `pa_area` | PA (Process Outputs) | Q (e.g., Q0.0) | Digital/analog outputs |
| `mk_area` | MK (Markers) | M (e.g., M0.0) | Internal flags/memory |

---

### logging Object

```json
{
  "logging": {
    "log_connections": true,
    "log_data_access": false,
    "log_errors": true
  }
}
```

| Field | Type | Required | Default | Description |
|-------|------|----------|---------|-------------|
| `log_connections` | boolean | No | `true` | Log client connect/disconnect |
| `log_data_access` | boolean | No | `false` | Log read/write operations (verbose) |
| `log_errors` | boolean | No | `true` | Log errors and warnings |

**Notes:**
- `log_data_access` generates high log volume - use for debugging only
- All logs go through the centralized logging system (visible in Editor)

---

## Complete Example Configurations

### Minimal Configuration

```json
{
  "server": {
    "enabled": true,
    "port": 102
  }
}
```

### Digital I/O Only

```json
{
  "server": {
    "enabled": true,
    "port": 102
  },
  "data_blocks": [
    {
      "db_number": 1,
      "description": "Digital Inputs",
      "size_bytes": 128,
      "mapping": {
        "type": "bool_input",
        "start_buffer": 0,
        "bit_addressing": true
      }
    },
    {
      "db_number": 2,
      "description": "Digital Outputs",
      "size_bytes": 128,
      "mapping": {
        "type": "bool_output",
        "start_buffer": 0,
        "bit_addressing": true
      }
    }
  ]
}
```

### Mixed I/O with System Areas

```json
{
  "server": {
    "enabled": true,
    "bind_address": "0.0.0.0",
    "port": 102,
    "max_clients": 16
  },
  "plc_identity": {
    "name": "Production PLC",
    "module_type": "CPU 315-2 PN/DP"
  },
  "data_blocks": [
    {
      "db_number": 10,
      "description": "Analog Inputs",
      "size_bytes": 256,
      "mapping": {
        "type": "int_input",
        "start_buffer": 0
      }
    },
    {
      "db_number": 20,
      "description": "Analog Outputs",
      "size_bytes": 256,
      "mapping": {
        "type": "int_output",
        "start_buffer": 0
      }
    },
    {
      "db_number": 100,
      "description": "Setpoints",
      "size_bytes": 512,
      "mapping": {
        "type": "int_memory",
        "start_buffer": 0
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

### Non-Privileged Port

For running without root privileges:

```json
{
  "server": {
    "enabled": true,
    "port": 1102,
    "max_clients": 8
  },
  "data_blocks": [
    {
      "db_number": 1,
      "size_bytes": 1024,
      "mapping": {
        "type": "int_memory",
        "start_buffer": 0
      }
    }
  ]
}
```

---

## Editor UI Recommendations

### Required Fields
- `server.enabled` - Toggle to enable/disable plugin
- `server.port` - Port number input (validate 1-65535)
- At least one data_block or system_area for meaningful operation

### Suggested UI Layout

1. **Server Settings Panel**
   - Enable/Disable toggle
   - Port number (default 102, warn if < 1024)
   - Max clients (slider 1-128)
   - Advanced: timeouts, PDU size (collapsible)

2. **PLC Identity Panel** (collapsible, optional)
   - Text fields for identity strings
   - Module type dropdown with common CPU types

3. **Data Blocks Panel**
   - Add/Remove data block buttons
   - For each block:
     - DB Number (required, unique)
     - Description (optional)
     - Size in bytes (required)
     - Mapping type dropdown
     - Start buffer index
     - Bit addressing checkbox (only for bool types)

4. **System Areas Panel** (collapsible)
   - PE Area (Inputs) - enable + size + mapping
   - PA Area (Outputs) - enable + size + mapping
   - MK Area (Markers) - enable + size + mapping

5. **Logging Panel** (collapsible)
   - Checkboxes for each log type

### Validation Rules

1. **DB Numbers**: Must be unique, 1-65535
2. **Sizes**: Must be > 0, <= 65536
3. **Port**: Warn if 102 (requires root), error if out of range
4. **Start Buffer**: Validate against buffer_size (typically 1024)
5. **Type Consistency**: Warn if mixing input types with write-heavy use cases

---

## Runtime Behavior

### Double-Buffering Architecture

The plugin uses double-buffering for thread safety:
- **S7 Buffer**: What Snap7 clients read/write (asynchronous access)
- **Shadow Buffer**: Used for synchronization with OpenPLC

Sync occurs at the end of each PLC scan cycle:
1. If no clients connected: skip sync entirely (optimization)
2. Copy S7 buffers -> shadow buffers (brief mutex lock)
3. Sync shadow <-> OpenPLC buffers
4. Copy shadow buffers -> S7 buffers (brief mutex lock)

### Data Direction

| Buffer Type | S7 Client Can Read | S7 Client Can Write |
|-------------|-------------------|---------------------|
| `*_input` | Yes | No (ignored) |
| `*_output` | Yes | Yes |
| `*_memory` | Yes | Yes |

### Byte Order

S7 protocol uses **big-endian** (network byte order). The plugin automatically converts between big-endian (S7) and little-endian (OpenPLC/x86).

---

*Schema Version: 1.0*
*Last Updated: 2026-01-12*
