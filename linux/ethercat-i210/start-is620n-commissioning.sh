#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Start only the pinned, tested IS620N controller. No enable/home/move is sent.
# Applies the custom PDO/DC/homing startup configuration to slave 0.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID == 0 ]] || { echo 'Run this reviewed commissioning launcher with sudo.' >&2; exit 1; }
[[ $# == 0 ]] || { echo 'No arguments accepted.' >&2; exit 1; }
readonly SOURCE=/home/a/st-workbench-i210/is620n-native
readonly TARGET=/run/st-workbench-is620n
readonly PROGRAM_DIR=/opt/st-workbench-is620n
readonly UNIT=st-workbench-is620n.service
readonly REPORT=/run/st-workbench-is620n-startup.txt
readonly TOOL=/opt/etherlab/bin/ethercat
if systemctl is-active --quiet "$UNIT"; then
    echo "$UNIT is already running; no restart performed." >&2; exit 1
fi
if [[ -e $TARGET/controller.sock ]]; then
    echo 'A controller socket already exists; inspect ownership before another startup.' >&2; exit 1
fi
# Windows and Linux build clocks differ; pin contents rather than timestamps.
echo '0919aeb1864d7b5ea9d0ff869f87379d5c3786a7c306a350ea2d11b1c886fdcd  /home/a/st-workbench-i210/is620n-native/build/ecmotion-rt' | sha256sum --check --status
echo 'f553bcc46e88d06a58947fd088b83f1cfed548bd4c4a455806b88a1f1a3d37da  /home/a/st-workbench-i210/is620n-native/config/is620n-machine.json' | sha256sum --check --status
echo '717fd374b8ec34faa4f687bae1fe331e8337134f020c401e150024db1e88131e  /home/a/st-workbench-i210/is620n-native/config/is620n-io-map.json' | sha256sum --check --status
master_status=$("$TOOL" master)
if ! [[ $master_status =~ Active:[[:space:]]+no ]]; then
    echo 'IgH master is already active or its state could not be verified; refusing takeover.' >&2; exit 1
fi
[[ $(getconf _NPROCESSORS_ONLN) -gt 2 ]] || { echo 'CPU 2 is unavailable.' >&2; exit 1; }
install -d -o root -g a -m 750 "$TARGET"
install -d -o root -g a -m 750 "$PROGRAM_DIR"
install -o root -g a -m 750 "$SOURCE/build/ecmotion-rt" "$PROGRAM_DIR/ecmotion-rt-0919aeb1864d"
install -o root -g a -m 640 "$SOURCE/config/is620n-machine.json" "$TARGET/machine.json"
install -o root -g a -m 640 "$SOURCE/config/is620n-io-map.json" "$TARGET/io-map.json"
if systemctl is-failed --quiet "$UNIT"; then systemctl reset-failed "$UNIT"; fi
if ! systemd-run --unit=st-workbench-is620n --service-type=exec \
    --property=User=root --property=Group=a --property=UMask=0027 \
    --property=LimitMEMLOCK=infinity --property=LimitRTPRIO=95 \
    --property=RuntimeMaxSec=infinity --property=TimeoutStopSec=5 \
    --property=KillMode=control-group --property=NoNewPrivileges=yes \
    "$PROGRAM_DIR/ecmotion-rt-0919aeb1864d" --cpu 2 --io-map "$TARGET/io-map.json" \
    --machine-config "$TARGET/machine.json" --socket "$TARGET/controller.sock"; then
    journalctl -u "$UNIT" -n 50 --no-pager > "$REPORT"
    chmod 644 "$REPORT"
    cat "$REPORT"
    exit 1
fi
for attempt in {1..50}; do
    [[ -S $TARGET/controller.sock ]] && break
    systemctl is-active --quiet "$UNIT" || break
    sleep 0.2
done
sleep 1
temporary_report=$(mktemp /run/st-workbench-is620n-startup.XXXXXX)
trap 'rm -f "$temporary_report"' EXIT
{
    date -Is
    echo 'Commissioning only: no Enable, Home or Move requested by launcher.'
    echo 'Service runs until explicitly stopped or host reboot. Emergency shutdown: sudo systemctl stop st-workbench-is620n.service'
    systemctl status "$UNIT" --no-pager || true
    "$TOOL" master || true
    "$TOOL" slaves --verbose || true
    journalctl -u "$UNIT" -n 50 --no-pager || true
} > "$temporary_report" 2>&1
install -m 644 "$temporary_report" "$REPORT"
cat "$REPORT"
systemctl is-active --quiet "$UNIT"
[[ -S $TARGET/controller.sock ]]
echo "Startup report saved: $REPORT"
