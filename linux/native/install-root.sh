#!/usr/bin/env bash
# Root captures and verifies all executables before publishing fixed privileged roles.
# This does not stop, start, restart or enable EtherCAT/motion services.
set -euo pipefail
umask 077
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ $(id -u) == 0 && $# == 2 && $2 =~ ^[0-9a-f]{64}$ ]] || { echo 'Usage as root: install-root.sh BUILD-DIRECTORY INVENTORY-SHA256' >&2; exit 2; }
source_directory=$(realpath -e -- "$1")
[[ -d $source_directory && ! -L $1 ]] || exit 1
for unit in stworkbench-hardware.service stworkbench-hardware.socket stworkbench-ethercat-restore.service; do
    if systemctl is-active --quiet "$unit"; then echo "Stop $unit before installation" >&2; exit 1; fi
done
capture=$(mktemp -d /var/tmp/st-native-install-XXXXXXXX)
trap 'rm -rf -- "$capture"' EXIT
[[ -f $source_directory/native-binaries.sha256 && ! -L $source_directory/native-binaries.sha256 ]] || exit 1
install -o root -g root -m 0600 -- "$source_directory/native-binaries.sha256" "$capture/inventory"
[[ $(sha256sum "$capture/inventory" | cut -d' ' -f1) == "$2" ]] || { echo 'Approved build inventory differs' >&2; exit 1; }
declare -A destinations=(
    [st-hardware-apply]=/usr/local/libexec/stworkbench-ethercat-apply
    [st-runtime-restore]=/usr/local/libexec/stworkbench-ethercat-restore
    [st-hardware-broker]=/usr/local/libexec/stworkbench-hardware-broker
    [st-workbench-cpu-isolation]=/usr/local/libexec/st-workbench-cpu-isolation
)
mkdir -m 0700 -- "$capture/units"
for name in stworkbench-hardware.service stworkbench-hardware.socket stworkbench-ethercat-restore.service; do
    [[ -f $source_directory/units/$name && ! -L $source_directory/units/$name ]] || exit 1
    install -o root -g root -m 0600 -- "$source_directory/units/$name" "$capture/units/$name"
    line=$(grep -E "^[0-9a-f]{64}  units/$name$" "$capture/inventory")
    [[ -n $line && $(printf '%s\n' "$line" | wc -l) == 1 ]] || exit 1
    (cd "$capture" && printf '%s\n' "$line" | sha256sum --strict -c - >/dev/null)
done
for name in "${!destinations[@]}"; do
    [[ -f $source_directory/$name && ! -L $source_directory/$name ]] || exit 1
    install -o root -g root -m 0500 -- "$source_directory/$name" "$capture/$name"
    line=$(grep -E "^[0-9a-f]{64}  $name$" "$capture/inventory")
    [[ -n $line && $(printf '%s\n' "$line" | wc -l) == 1 ]] || exit 1
    (cd "$capture" && printf '%s\n' "$line" | sha256sum --strict -c - >/dev/null)
    [[ $(od -An -tx1 -N4 "$capture/$name" | tr -d ' \n') == 7f454c46 ]] || { echo 'ELF executable required' >&2; exit 1; }
done
for directory in /usr/local/libexec /etc/systemd/system; do
    for path in "$directory" "$(dirname "$directory")" /usr /etc /; do
        [[ ! -L $path && $(stat -c %u "$path") == 0 && $(stat -c %a "$path") =~ ^[0-7]*[0145][0145]$ ]] || { echo 'Root-controlled directory required' >&2; exit 1; }
    done
done
for name in "${!destinations[@]}"; do
    destination=${destinations[$name]}
    [[ ! -L $destination ]] || exit 1
    temporary=$(mktemp /usr/local/libexec/.st-native-XXXXXXXX)
    install -o root -g root -m 0755 -- "$capture/$name" "$temporary"
    mv -T -- "$temporary" "$destination"
done
getent group stworkbench-hardware >/dev/null || groupadd --system stworkbench-hardware
usermod -a -G stworkbench-hardware a
for name in stworkbench-hardware.service stworkbench-hardware.socket stworkbench-ethercat-restore.service; do
    [[ ! -L /etc/systemd/system/$name ]] || exit 1
    install -o root -g root -m 0644 -- "$capture/units/$name" "/etc/systemd/system/$name"
done
systemctl daemon-reload
echo 'Approved native privileged roles installed; services remain stopped. Group membership applies to a new user session.'
