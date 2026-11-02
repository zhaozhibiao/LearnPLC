#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Read-only EtherCAT identity/SDO probe. No enable, state changes or SDO downloads.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID == 0 ]] || { echo 'Run this probe with sudo.' >&2; exit 1; }
readonly TOOL=/opt/etherlab/bin/ethercat
readonly REPORT=/run/st-workbench-is620n-probe.txt
readonly TEMP=$(mktemp /run/st-workbench-is620n-probe.XXXXXX)
trap 'rm -f "$TEMP"' EXIT
{
    date -Is
    "$TOOL" master
    "$TOOL" slaves --verbose
    "$TOOL" pdos --position 0
    for request in 'string 0x1008 0' 'uint32 0x1018 1' 'uint32 0x1018 2' 'uint32 0x1018 3' \
        'uint16 0x603f 0' 'uint16 0x6041 0' 'int8 0x6061 0' 'int32 0x6064 0' 'int32 0x606c 0' \
        'uint32 0x608f 1' 'uint32 0x608f 2' 'uint32 0x6091 1' 'uint32 0x6091 2' \
        'uint32 0x6092 1' 'uint32 0x6092 2' 'int8 0x6098 0' 'uint32 0x6502 0'; do
        read -r type index subindex <<< "$request"
        echo "=== SDO $index:$subindex ($type) ==="
        "$TOOL" upload --position 0 --type "$type" "$index" "$subindex" || echo 'READ_UNAVAILABLE (no parameter changed)'
    done
} > "$TEMP" 2>&1
install -m 644 "$TEMP" "$REPORT"
cat "$REPORT"
echo "Read-only report saved: $REPORT"
