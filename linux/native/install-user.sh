#!/usr/bin/env bash
# Install a verified native build without starting a service or changing PLC state.
set -euo pipefail
umask 077
[[ $# == 2 && $2 =~ ^[0-9a-f]{64}$ ]] || { echo 'Usage: install-user.sh BUILD-DIRECTORY INVENTORY-SHA256' >&2; exit 2; }
[[ $(id -un) == a && $HOME == /home/a ]] || { echo 'Expected ordinary device user a' >&2; exit 1; }
staging=$(realpath -e -- "$1")
[[ -d $staging && ! -L $1 ]] || exit 1
capture=$(mktemp -d /tmp/st-native-user-install-XXXXXXXX)
trap 'rm -rf -- "$capture"' EXIT
[[ -f $staging/native-binaries.sha256 && ! -L $staging/native-binaries.sha256 ]] || exit 1
install -m 0600 -- "$staging/native-binaries.sha256" "$capture/native-binaries.sha256"
inventory=$capture/native-binaries.sha256
[[ $(sha256sum -- "$inventory" | cut -d' ' -f1) == "$2" ]] || { echo 'Build inventory identity differs' >&2; exit 1; }
prefix=$HOME/.local/share/st-workbench/runtime-v4.3.0
for unit in stworkbench-openplc stworkbench-engineering stworkbench-is620n-axis stworkbench-axis-v2 stworkbench-communication stworkbench-motion; do
    if systemctl --user is-active --quiet "$unit.service"; then echo "Stop $unit before installation" >&2; exit 1; fi
done
if pgrep -u "$(id -u)" -x plc_main >/dev/null; then echo 'A PLC process is still alive; installation refused' >&2; exit 1; fi
for target in st-runtime-host st-engineering-gateway st-runtime-startup-gate st-session-helper st-deployment-coordinator st-motion-gateway st-axis-worker st-communication-worker st-path-worker st-axis-link-probe plc_main; do
    [[ -f $staging/$target && ! -L $staging/$target ]] || { echo "Missing native executable: $target" >&2; exit 1; }
    install -m 0700 -- "$staging/$target" "$capture/$target"
    line=$(grep -E "^[0-9a-f]{64}  $target$" "$inventory")
    [[ $(printf '%s\n' "$line" | wc -l) == 1 && -n $line ]] || exit 1
    (cd "$capture" && printf '%s\n' "$line" | sha256sum --strict -c - >/dev/null)
    [[ $(od -An -tx1 -N4 "$capture/$target" | tr -d ' \n') == 7f454c46 ]] || { echo 'ELF executable required' >&2; exit 1; }
done
[[ -f $staging/plc-core-receipt.json && ! -L $staging/plc-core-receipt.json ]] || exit 1
install -m 0600 -- "$staging/plc-core-receipt.json" "$capture/plc-core-receipt.json"
line=$(grep -E '^[0-9a-f]{64}  plc-core-receipt.json$' "$inventory")
[[ -n $line && $(printf '%s\n' "$line" | wc -l) == 1 ]] || exit 1
(cd "$capture" && printf '%s\n' "$line" | sha256sum --strict -c - >/dev/null)
if readelf -d "$capture/plc_main" | grep -qi 'NEEDED.*python'; then echo 'Embedded interpreter dependency refused' >&2; exit 1; fi
units=$HOME/.config/systemd/user
mkdir -m 0700 -- "$capture/units"
for unit in stworkbench-openplc.service stworkbench-engineering.service stworkbench-is620n-axis.service stworkbench-axis-v2.service native-workers.conf; do
    [[ -f $staging/units/$unit && ! -L $staging/units/$unit && ! -L $units/$unit ]] || exit 1
    install -m 0600 -- "$staging/units/$unit" "$capture/units/$unit"
    line=$(grep -E "^[0-9a-f]{64}  units/$unit$" "$inventory")
    [[ -n $line && $(printf '%s\n' "$line" | wc -l) == 1 ]] || exit 1
    (cd "$capture" && printf '%s\n' "$line" | sha256sum --strict -c - >/dev/null)
done
dropins=$units/stworkbench-openplc.service.d
[[ ! -L $dropins && ! -L $dropins/communication.conf && ! -L $dropins/native-workers.conf && ! -L $dropins/native-communication-environment.conf ]] || exit 1
legacy_environment=
if [[ -f $dropins/communication.conf ]]; then
    awk '
      /^\[Service\]$/ {next}
      /^Environment=STWORKBENCH_COMM_SOCKET=\/home\/a\/[A-Za-z0-9_./-]+$/ {next}
      /^ExecStartPre=\/usr\/bin\/python3 \/home\/a\/st-workbench-deployment\/communication-client\/service_lifecycle.py start-active$/ {next}
      /^ExecStopPost=\/usr\/bin\/python3 \/home\/a\/st-workbench-deployment\/communication-client\/service_lifecycle.py stop$/ {next}
      /^$/ {next}
      {exit 1}
    ' "$dropins/communication.conf" || { echo 'Unrecognized legacy lifecycle drop-in; installation refused' >&2; exit 1; }
    legacy_environment=$(sed -n '/^Environment=STWORKBENCH_COMM_SOCKET=/p' "$dropins/communication.conf")
fi
for directory in "$prefix/native/bin" "$prefix/engineering" "$units" "$dropins" "$HOME/st-workbench-deployment" "$HOME/st-workbench-mcp" "$HOME/st-workbench-runtime-install"; do
    [[ ! -L $directory ]] || exit 1
    install -d -m 0700 -- "$directory"
done
install -m 0600 -- "$capture/units/native-workers.conf" "$dropins/native-workers.conf"
if [[ -n $legacy_environment ]]; then printf '[Service]\n%s\n' "$legacy_environment" > "$dropins/native-communication-environment.conf"; fi
if [[ -f $dropins/communication.conf ]]; then rm -- "$dropins/communication.conf"; fi
publish() {
    local source=$1 destination=$2 temporary
    [[ ! -L $destination ]] || exit 1
    temporary=$(mktemp "$(dirname "$destination")/.st-native-XXXXXXXX")
    install -m 0700 -- "$source" "$temporary"
    mv -T -- "$temporary" "$destination"
}
for target in st-runtime-host st-engineering-gateway st-runtime-startup-gate st-axis-worker st-communication-worker st-path-worker st-axis-link-probe; do
    publish "$capture/$target" "$prefix/native/bin/$target"
done
publish "$capture/st-deployment-coordinator" "$HOME/st-workbench-deployment/st-deployment-coordinator"
publish "$capture/st-motion-gateway" "$HOME/st-workbench-mcp/st-motion-gateway"
publish "$capture/st-session-helper" "$HOME/st-workbench-runtime-install/st-session-helper"
[[ -d $prefix/source/build && ! -L $prefix/source/build ]] || exit 1
publish "$capture/plc_main" "$prefix/source/build/plc_main"
install -m 0600 -- "$capture/plc-core-receipt.json" "$prefix/native/plc-core-receipt.json"
unit_source=$capture/units
for unit in stworkbench-openplc.service stworkbench-engineering.service stworkbench-is620n-axis.service stworkbench-axis-v2.service; do
    [[ -f $unit_source/$unit && ! -L $unit_source/$unit && ! -L $units/$unit ]] || exit 1
    install -m 0600 -- "$unit_source/$unit" "$units/$unit"
done
for axis in is620n-axis axis-v2; do
    target=$HOME/.local/share/st-workbench/$axis
    [[ ! -L $target ]] || exit 1
    install -d -m 0700 -- "$target"
    publish "$capture/st-axis-worker" "$target/st-axis-worker"
done
install -m 0600 -- "$inventory" "$prefix/native/native-binaries.sha256"
systemctl --user daemon-reload
echo 'Native files and units installed. No service started; candidate identity receipts must be applied before activation.'
