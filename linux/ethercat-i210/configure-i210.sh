#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Host-specific installation. --check makes no system changes.
set -Eeuo pipefail
readonly ROOT=/home/a/st-workbench-i210
readonly SOURCE="$ROOT/ethercat-1.6.13"
readonly KERNEL=6.12.111+deb13-rt-amd64
readonly COMMIT=61cc654f5b721ddd54df0f58bdd34106d91c5359
readonly NIC=enp3s0 MAC=b4:96:91:77:df:0f PCI=0000:03:00.0
readonly PROFILE=252c14c9-0687-33a1-8a78-de58cbf70d1a
readonly BACKUP=/var/lib/st-workbench-i210
readonly NMCONF=/etc/NetworkManager/conf.d/90-st-workbench-i210.conf
readonly ORDER=/etc/systemd/system/ethercat.service.d/90-i210-order.conf
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

fail() {
    echo "ERROR: $*" >&2
    if [[ ${applying:-0} == 1 ]]; then applying=0; rollback || true; fi
    exit 1
}
save_file() {
    local file=$1 key=$2
    if [[ -e "$file" ]]; then cp -a -- "$file" "$BACKUP/$key"; else touch "$BACKUP/$key.absent"; fi
}
restore_file() {
    local file=$1 key=$2
    if [[ -f "$BACKUP/$key.absent" ]]; then rm -f -- "$file";
    elif [[ -f "$BACKUP/$key" ]]; then install -m 644 "$BACKUP/$key" "$file"; fi
}
rollback() {
    [[ $EUID == 0 && -f "$BACKUP/prepared" ]] || fail 'No installation backup; rollback refused.'
    systemctl disable --now ethercat.service || true
    modprobe -r ec_igb ec_master 2>/dev/null || true
    modprobe igb
    restore_file /etc/ethercat.conf ethercat.conf
    restore_file "$NMCONF" network.conf
    restore_file "$ORDER" ordering.conf
    nmcli connection modify uuid "$PROFILE" connection.autoconnect "$(cat "$BACKUP/autoconnect")"
    systemctl daemon-reload
    nmcli general reload
    nmcli device set "$NIC" managed yes || true
    rm -f /run/st-workbench-i210-status.txt
    echo 'Rolled back interface ownership. Installed IgH files remain on disk; master is disabled.'
}

repair_permissions() {
    [[ $EUID == 0 ]] || fail 'Use sudo for --repair-permissions.'
    [[ $(uname -r) == "$KERNEL" && -f "$BACKUP/completed" ]] || fail 'Expected completed installation and kernel.'
    [[ -d /opt/etherlab && ! -L /opt/etherlab ]] || fail 'Unexpected installation path.'
    grep -Fxq "MASTER0_DEVICE=\"$MAC\"" /etc/ethercat.conf || fail 'Master binding differs.'
    # These trees contain installed programs/libraries/modules, never credentials.
    find /opt/etherlab -type d -exec chmod 755 {} +
    if [[ -d /lib/modules/$KERNEL/ethercat && ! -L /lib/modules/$KERNEL/ethercat ]]; then
        find "/lib/modules/$KERNEL/ethercat" -type d -exec chmod 755 {} +
        find "/lib/modules/$KERNEL/ethercat" -type f -name '*.ko*' -exec chmod 644 {} +
    fi
    chmod 644 /etc/ethercat.conf "$NMCONF" "$ORDER"
    # The first installer also generated module indexes under its restrictive umask.
    umask 022
    depmod -a "$KERNEL"
    /opt/etherlab/bin/ethercat master > "$BACKUP/master-after-install.txt"
    install -m 644 "$BACKUP/master-after-install.txt" /run/st-workbench-i210-status.txt
    cat "$BACKUP/master-after-install.txt"
    echo 'Installed directory/module permissions repaired. Device permissions and services unchanged.'
}

mode=${1:---check}
if [[ $mode == --rollback ]]; then rollback; exit; fi
if [[ $mode == --repair-permissions ]]; then repair_permissions; exit; fi
[[ $mode == --check || $mode == --apply ]] || fail 'Use --check, --apply, --rollback or --repair-permissions.'
[[ $(uname -r) == "$KERNEL" ]] || fail 'Kernel changed; rebuild and review first.'
[[ -d /sys/class/net/$NIC && $(cat /sys/class/net/$NIC/address) == "$MAC" ]] || fail 'I210 interface/MAC differs.'
[[ $(basename "$(readlink -f /sys/class/net/$NIC/device)") == "$PCI" ]] || fail 'PCI address differs.'
[[ $(cat /sys/class/net/$NIC/device/vendor) == 0x8086 && $(cat /sys/class/net/$NIC/device/device) == 0x1533 ]] || fail 'Expected Intel I210 8086:1533.'
[[ $(basename "$(readlink -f /sys/class/net/$NIC/device/driver)") == igb ]] || fail 'Expected stock igb before first apply.'
[[ ! -e /sys/class/net/$NIC/master ]] || fail 'I210 is a bridge/bond member.'
if ip -o -4 address show dev "$NIC" | grep -q ' inet '; then fail 'I210 has an IPv4 address; review before disconnecting.'; fi
if ip -o -6 address show dev "$NIC" scope global | grep -q ' inet6 '; then fail 'I210 has a global IPv6 address; review before disconnecting.'; fi
if ip -4 route show table all | grep -Eq "(^| )dev $NIC( |$)"; then fail 'I210 has IPv4 routes; refusing to disrupt them.'; fi
if ip -6 route show default | grep -Eq "(^| )dev $NIC( |$)"; then fail 'I210 has an IPv6 default route; review first.'; fi
for other in /sys/class/net/*; do
    [[ $(basename "$other") == "$NIC" ]] && continue
    [[ $(basename "$(readlink -f "$other/device/driver" 2>/dev/null)") != igb ]] || fail 'Another igb NIC exists; native driver replacement requires a new review.'
done
[[ $(nmcli -g connection.interface-name connection show uuid "$PROFILE") == "$NIC" ]] || fail 'NetworkManager profile binding differs.'
[[ ! -e /opt/etherlab && ! -e "$BACKUP" ]] || fail 'Existing installation/backup; do not overwrite.'
for unit in ethercat.service ecmotion-rt.service ecmotion-plc.service; do
    if systemctl is-active --quiet "$unit"; then fail "$unit is active; existing control must be reviewed first."; fi
done
[[ ! -d /sys/module/ec_master ]] || fail 'An EtherCAT master is already loaded.'
[[ $(git -c safe.directory="$SOURCE" -C "$SOURCE" rev-parse HEAD) == "$COMMIT" ]] || fail 'IgH source pin differs.'
git -c safe.directory="$SOURCE" -C "$SOURCE" diff --exit-code --quiet
for module in master/ec_master.ko devices/igb/ec_igb.ko; do
    [[ $(modinfo -F vermagic "$SOURCE/$module") == "$KERNEL "* ]] || fail 'Module kernel ABI differs.'
done
(cd "$SOURCE" && sha256sum --check --quiet "$ROOT/built-files.sha256")
echo "Verified I210 $NIC / $MAC / $PCI, IgH 1.6.13 native igb, kernel $KERNEL."
echo 'Management enp2s0 is excluded. Planned: disable I210 IP management, install master, enable only EtherCAT kernel service.'
[[ $mode == --apply ]] || exit 0
[[ $EUID == 0 ]] || fail 'Run sudo bash /home/a/st-workbench-i210/configure-i210.sh --apply in your Linux terminal.'

umask 077
install -d -m 700 "$BACKUP"
save_file /etc/ethercat.conf ethercat.conf
save_file "$NMCONF" network.conf
save_file "$ORDER" ordering.conf
nmcli -g connection.autoconnect connection show uuid "$PROFILE" > "$BACKUP/autoconnect"
touch "$BACKUP/prepared"
applying=1
trap 'code=$?; trap - ERR; echo "Install failed; restoring NIC ownership." >&2; rollback || true; exit "$code"' ERR
umask 022
make -C "$SOURCE" install
make -C "$SOURCE" modules_install
ldconfig
depmod -a "$KERNEL"
install -d /etc/NetworkManager/conf.d /etc/systemd/system/ethercat.service.d
cat > "$NMCONF" <<EOF
[device-st-workbench-ethercat-i210]
match-device=mac:$MAC
managed=0
EOF
cat > /etc/ethercat.conf <<EOF
# Dedicated Intel I210, configured by ST Workbench.
MASTER0_DEVICE="$MAC"
DEVICE_MODULES="igb"
UPDOWN_INTERFACES=""
EOF
cat > "$ORDER" <<'EOF'
[Unit]
Before=network-pre.target
Wants=network-pre.target
EOF
nmcli connection modify uuid "$PROFILE" connection.autoconnect no
nmcli device disconnect "$NIC" || true
nmcli general reload
nmcli device set "$NIC" managed no
systemctl daemon-reload
systemctl enable --now ethercat.service
/opt/etherlab/bin/ethercat master > "$BACKUP/master-after-install.txt"
cat "$BACKUP/master-after-install.txt"
[[ $(cat /sys/bus/pci/devices/$PCI/vendor) == 0x8086 ]]
[[ $(basename "$(readlink -f /sys/bus/pci/devices/$PCI/driver)") == ec_igb ]] || fail 'Native EtherCAT driver did not claim I210.'
ip -br address show dev enp2s0
touch "$BACKUP/completed"
install -m 644 "$BACKUP/master-after-install.txt" /run/st-workbench-i210-status.txt
trap - ERR
echo 'I210 EtherCAT dedication complete. No motion controller or PLC program was started.'
echo 'Rollback: sudo bash /home/a/st-workbench-i210/configure-i210.sh --rollback'
