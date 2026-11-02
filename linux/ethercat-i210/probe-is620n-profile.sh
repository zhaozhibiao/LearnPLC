#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Extended READ-ONLY commissioning probe: no downloads, states or motor commands.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID == 0 ]] || { echo 'Run this read-only probe with sudo.' >&2; exit 1; }
readonly TOOL=/opt/etherlab/bin/ethercat
readonly REPORT=/run/st-workbench-is620n-profile.txt
readonly TEMP=$(mktemp /run/st-workbench-is620n-profile.XXXXXX)
trap 'rm -f "$TEMP"' EXIT
read_sdo() {
    local type=$1 index=$2 subindex=$3
    echo "=== SDO $index:$subindex ($type) ==="
    "$TOOL" upload --position 0 --type "$type" "$index" "$subindex" || echo 'READ_UNAVAILABLE (no parameter changed)'
}
{
    date -Is
    "$TOOL" master
    "$TOOL" slaves --verbose
    "$TOOL" pdos --position 0
    # Fixed PDO layouts must be read rather than copied from another drive.
    for index in 0x1703 0x1b04 0x1600 0x1a00; do
        read_sdo uint8 "$index" 0
        for subindex in 1 2 3 4 5 6 7 8 9 10; do
            read_sdo uint32 "$index" "$subindex"
        done
    done
    for request in 'uint16 0x1c32 1' 'uint32 0x1c32 2' 'uint16 0x1c32 4' \
        'uint32 0x1c32 5' 'uint16 0x1c33 1' 'uint16 0x1c33 4' \
        'uint32 0x6091 1' 'uint32 0x6091 2' 'int32 0x6063 0' 'int32 0x6064 0' \
        'uint16 0x60e0 0' 'uint16 0x60e1 0' 'int8 0x6098 0' 'int32 0x607c 0' \
        'uint16 0x603f 0' 'uint16 0x6041 0' 'int8 0x6061 0' 'uint32 0x6502 0'; do
        read -r type index subindex <<< "$request"
        read_sdo "$type" "$index" "$subindex"
    done
    echo '=== SII XML (read only) ==='
    "$TOOL" xml --position 0 || echo 'SII_XML_UNAVAILABLE'
} > "$TEMP" 2>&1
install -m 644 "$TEMP" "$REPORT"
cat "$REPORT"
echo "Read-only profile report saved: $REPORT"
