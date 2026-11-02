# S7Comm Plugin User Guide

## Overview

The S7Comm plugin enables OpenPLC Runtime v4 to communicate using the Siemens S7 protocol. This allows S7-compatible HMIs, SCADA systems, and other industrial equipment to read and write data from/to the OpenPLC runtime.

## Features

- Full S7Comm protocol support via Snap7 library
- Configurable data block mappings
- Support for all OpenPLC buffer types (BOOL, BYTE, UINT, UDINT, ULINT)
- Multiple concurrent client connections
- Configurable PLC identity for S7 client compatibility
- Double-buffered, thread-safe operation (S7 clients run asynchronously from PLC cycle)
- Automatic optimization: no overhead when no clients are connected
- Centralized logging integrated with OpenPLC Editor

## Quick Start

### 1. Enable the Plugin

Add or uncomment the S7Comm entry in `plugins.conf`:

```
s7comm,./core/src/drivers/plugins/native/s7comm/libs7comm.so,1,1,./core/src/drivers/plugins/native/s7comm/s7comm_config.json,
```

### 2. Configure the Plugin

Edit `s7comm_config.json` to match your requirements:

```json
{
  "server": {
    "enabled": true,
    "port": 102
  },
  "data_blocks": [
    {
      "db_number": 1,
      "size_bytes": 128,
      "mapping": {
        "type": "bool_input",
        "start_buffer": 0
      }
    }
  ]
}
```

### 3. Start the Runtime

```bash
sudo ./start_openplc.sh
```

### 4. Connect with S7 Client

The plugin listens on port 102 (default S7 port). Connect using any S7-compatible client.

## Configuration Reference

### Server Settings

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

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | true | Enable/disable the S7 server |
| `bind_address` | "0.0.0.0" | Network interface to bind (0.0.0.0 = all) |
| `port` | 102 | TCP port (102 is standard S7 port) |
| `max_clients` | 32 | Maximum simultaneous connections |
| `work_interval_ms` | 100 | Internal polling interval |
| `send_timeout_ms` | 3000 | Socket send timeout |
| `recv_timeout_ms` | 3000 | Socket receive timeout |
| `ping_timeout_ms` | 10000 | Keep-alive timeout |
| `pdu_size` | 480 | Maximum PDU size (240-960) |

**Note:** Port 102 requires root privileges on Linux. Use `sudo` or configure capabilities.

### PLC Identity

Configure how the PLC identifies itself to S7 clients:

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

These values are returned when clients query the PLC's system state list (SZL).

### Data Block Mappings

Data blocks map S7 DB areas to OpenPLC memory buffers:

```json
{
  "data_blocks": [
    {
      "db_number": 10,
      "description": "Analog Inputs",
      "size_bytes": 2048,
      "mapping": {
        "type": "int_input",
        "start_buffer": 0,
        "bit_addressing": false
      }
    }
  ]
}
```

| Field | Description |
|-------|-------------|
| `db_number` | S7 DB number (1-65535) |
| `description` | Human-readable description (optional) |
| `size_bytes` | Size of the data block in bytes |
| `mapping.type` | OpenPLC buffer type to map to |
| `mapping.start_buffer` | Starting index in OpenPLC buffer |
| `mapping.bit_addressing` | Enable bit-level access (for BOOL types) |

### Supported Buffer Types

| Type | IEC Address | Element Size | Max Count |
|------|-------------|--------------|-----------|
| `bool_input` | %IX | 1 bit | 8192 |
| `bool_output` | %QX | 1 bit | 8192 |
| `bool_memory` | %MX | 1 bit | 8192 |
| `byte_input` | %IB | 1 byte | 1024 |
| `byte_output` | %QB | 1 byte | 1024 |
| `int_input` | %IW | 2 bytes | 1024 |
| `int_output` | %QW | 2 bytes | 1024 |
| `int_memory` | %MW | 2 bytes | 1024 |
| `dint_input` | %ID | 4 bytes | 1024 |
| `dint_output` | %QD | 4 bytes | 1024 |
| `dint_memory` | %MD | 4 bytes | 1024 |
| `lint_input` | %IL | 8 bytes | 1024 |
| `lint_output` | %QL | 8 bytes | 1024 |
| `lint_memory` | %ML | 8 bytes | 1024 |

### System Areas

Configure S7 system areas (PE, PA, MK):

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

| Area | S7 Address | Description |
|------|------------|-------------|
| `pe_area` | I (Inputs) | Process inputs |
| `pa_area` | Q (Outputs) | Process outputs |
| `mk_area` | M (Markers) | Internal flags/markers |

### Logging

Configure logging verbosity:

```json
{
  "logging": {
    "log_connections": true,
    "log_data_access": false,
    "log_errors": true
  }
}
```

| Setting | Description |
|---------|-------------|
| `log_connections` | Log client connect/disconnect events |
| `log_data_access` | Log read/write operations (verbose) |
| `log_errors` | Log errors and warnings |

## Example Configurations

### Minimal Configuration

Basic setup with just digital I/O:

```json
{
  "server": {
    "enabled": true,
    "port": 102
  },
  "data_blocks": [
    {
      "db_number": 1,
      "size_bytes": 128,
      "mapping": { "type": "bool_input", "start_buffer": 0 }
    },
    {
      "db_number": 2,
      "size_bytes": 128,
      "mapping": { "type": "bool_output", "start_buffer": 0 }
    }
  ]
}
```

### Full Industrial Setup

Complete configuration with analog and digital I/O:

```json
{
  "server": {
    "enabled": true,
    "bind_address": "0.0.0.0",
    "port": 102,
    "max_clients": 16,
    "pdu_size": 480
  },
  "plc_identity": {
    "name": "Production Line PLC",
    "module_type": "CPU 315-2 PN/DP"
  },
  "data_blocks": [
    {
      "db_number": 1,
      "description": "Digital Inputs",
      "size_bytes": 128,
      "mapping": { "type": "bool_input", "start_buffer": 0, "bit_addressing": true }
    },
    {
      "db_number": 2,
      "description": "Digital Outputs",
      "size_bytes": 128,
      "mapping": { "type": "bool_output", "start_buffer": 0, "bit_addressing": true }
    },
    {
      "db_number": 10,
      "description": "Analog Inputs (4-20mA)",
      "size_bytes": 256,
      "mapping": { "type": "int_input", "start_buffer": 0 }
    },
    {
      "db_number": 20,
      "description": "Analog Outputs",
      "size_bytes": 256,
      "mapping": { "type": "int_output", "start_buffer": 0 }
    },
    {
      "db_number": 100,
      "description": "Setpoints",
      "size_bytes": 512,
      "mapping": { "type": "int_memory", "start_buffer": 0 }
    },
    {
      "db_number": 200,
      "description": "Counters/Timers",
      "size_bytes": 1024,
      "mapping": { "type": "dint_memory", "start_buffer": 0 }
    }
  ],
  "system_areas": {
    "pe_area": { "enabled": true, "size_bytes": 128, "mapping": { "type": "bool_input", "start_buffer": 0 } },
    "pa_area": { "enabled": true, "size_bytes": 128, "mapping": { "type": "bool_output", "start_buffer": 0 } },
    "mk_area": { "enabled": true, "size_bytes": 256, "mapping": { "type": "bool_memory", "start_buffer": 0 } }
  },
  "logging": {
    "log_connections": true,
    "log_data_access": false,
    "log_errors": true
  }
}
```

### WinCC-Compatible Configuration

Configuration matching typical WinCC tag addressing:

```json
{
  "server": {
    "enabled": true,
    "port": 102
  },
  "plc_identity": {
    "name": "OpenPLC",
    "module_type": "CPU 315-2 PN/DP"
  },
  "data_blocks": [
    {
      "db_number": 1,
      "description": "Process Data",
      "size_bytes": 4096,
      "mapping": { "type": "int_memory", "start_buffer": 0 }
    }
  ],
  "system_areas": {
    "pe_area": { "enabled": true, "size_bytes": 256, "mapping": { "type": "bool_input", "start_buffer": 0 } },
    "pa_area": { "enabled": true, "size_bytes": 256, "mapping": { "type": "bool_output", "start_buffer": 0 } },
    "mk_area": { "enabled": true, "size_bytes": 256, "mapping": { "type": "bool_memory", "start_buffer": 0 } }
  }
}
```

## S7 Address Mapping

### Understanding S7 Addresses

| S7 Address | Area | Description | OpenPLC Equivalent |
|------------|------|-------------|-------------------|
| I0.0 | PE | Input byte 0, bit 0 | %IX0.0 |
| Q10.5 | PA | Output byte 10, bit 5 | %QX10.5 |
| M100.0 | MK | Marker byte 100, bit 0 | %MX100.0 |
| DB1.DBX0.0 | DB | DB1, byte 0, bit 0 | Configured mapping |
| DB10.DBW0 | DB | DB10, word at byte 0 | Configured mapping |
| DB10.DBD4 | DB | DB10, dword at byte 4 | Configured mapping |

### Address Calculation

For data blocks with word mappings:

```
OpenPLC Buffer Index = start_buffer + (S7_byte_offset / element_size)

Example:
- DB10 mapped to int_input, start_buffer = 0
- S7 access: DB10.DBW10 (word at byte 10)
- OpenPLC: int_input[0 + (10/2)] = int_input[5]
```

For data blocks with bit mappings:

```
OpenPLC Buffer Index = start_buffer + S7_byte_offset
OpenPLC Bit Index = S7_bit_offset

Example:
- DB1 mapped to bool_output, start_buffer = 0
- S7 access: DB1.DBX5.3 (byte 5, bit 3)
- OpenPLC: bool_output[0 + 5][3] = bool_output[5][3]
```

## Connecting S7 Clients

### Connection Parameters

| Parameter | Value |
|-----------|-------|
| IP Address | OpenPLC host IP |
| Rack | 0 |
| Slot | 0 (or 1, 2) |
| Port | 102 (default) |

### Python (python-snap7)

```python
import snap7

client = snap7.client.Client()
client.connect('192.168.1.100', 0, 0)  # IP, rack, slot

# Read DB10, 10 bytes starting at byte 0
data = client.db_read(10, 0, 10)

# Write to DB20
client.db_write(20, 0, bytearray([0x01, 0x02, 0x03]))

# Read inputs (PE area)
inputs = client.read_area(snap7.types.Areas.PE, 0, 0, 10)

# Write outputs (PA area)
client.write_area(snap7.types.Areas.PA, 0, 0, bytearray([0xFF]))

client.disconnect()
```

### Node.js (node-snap7)

```javascript
const snap7 = require('node-snap7');

const client = new snap7.S7Client();
client.ConnectTo('192.168.1.100', 0, 0, (err) => {
    if (err) throw err;

    // Read DB10
    client.DBRead(10, 0, 10, (err, data) => {
        console.log(data);
    });

    // Write to DB20
    client.DBWrite(20, 0, Buffer.from([0x01, 0x02]), (err) => {
        if (!err) console.log('Written');
    });
});
```

### WinCC / TIA Portal

1. Add a new connection to the PLC
2. Set connection type to "S7 connection"
3. Enter OpenPLC IP address
4. Set Rack=0, Slot=0
5. Configure tags using DB addresses matching your configuration

## Troubleshooting

### Server Won't Start

**Symptom:** Plugin fails to initialize

**Solutions:**
1. Check port availability: `netstat -an | grep 102`
2. Verify root privileges for port 102
3. Check configuration file syntax
4. Review logs for specific errors

### Connection Refused

**Symptom:** Clients cannot connect

**Solutions:**
1. Verify server is running (check logs)
2. Check firewall rules for port 102
3. Verify bind_address allows external connections
4. Check max_clients limit

### Data Not Updating

**Symptom:** Values don't change in HMI

**Solutions:**
1. Verify DB number matches configuration
2. Check address offsets are within size_bytes
3. Verify PLC program is writing to correct addresses
4. Enable log_data_access for debugging

### Wrong Values

**Symptom:** Data appears corrupted

**Solutions:**
1. Check byte order (S7 uses big-endian)
2. Verify data type matches (Word vs DWord)
3. Check buffer mapping configuration
4. Verify bit_addressing setting for BOOL types

### Performance Issues

**Symptom:** Slow response times

**Solutions:**
1. Reduce log_data_access (very verbose)
2. Increase work_interval_ms
3. Reduce number of concurrent clients
4. Check network latency

## Limitations

1. **Counter/Timer Areas**: CT and TM areas are not currently supported
2. **Block Upload/Download**: Block transfer operations are not supported
3. **Password Protection**: S7 password protection is not implemented
4. **Multiple Instances**: Only one S7 server instance per runtime

## Security Considerations

1. **No Authentication**: S7Comm has no built-in authentication
2. **Network Segmentation**: Isolate S7 traffic on industrial network
3. **Firewall Rules**: Restrict access to port 102
4. **Read-Only Mode**: Consider mapping sensitive areas as read-only

## Architecture

### Double-Buffering

The plugin uses a double-buffering architecture for thread safety:

```
S7 Clients <---> [S7 Buffer] <---> [Shadow Buffer] <---> OpenPLC Buffers
                     ^                    ^
                     |                    |
              Snap7 server          PLC cycle sync
            (asynchronous)         (at cycle_end)
```

- **S7 Buffer**: Registered with Snap7, accessed asynchronously by S7 clients
- **Shadow Buffer**: Used for synchronization with OpenPLC at cycle end
- **Sync**: Happens only at cycle_end with brief mutex locks (minimal contention)
- **Optimization**: If no clients are connected, sync is skipped entirely

### Data Flow

1. S7 clients read/write to S7 buffers at any time (lock-free)
2. At end of PLC scan cycle:
   - S7 buffer -> Shadow buffer (capture client writes)
   - Shadow buffer <-> OpenPLC buffers (apply writes, get new values)
   - Shadow buffer -> S7 buffer (publish to clients)

## Related Documentation

- [JSON Configuration Schema](JSON_SCHEMA.md) - Detailed schema reference for Editor developers

## Support

For issues and feature requests:
- GitHub Issues: https://github.com/Autonomy-Logic/openplc-runtime/issues

---

*Document Version: 1.1*
*Last Updated: 2026-01-12*
