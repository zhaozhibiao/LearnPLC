#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Installs a pinned generic core + fixed privileged configuration helper only.
# Does not stop/start any service, configure a drive or send motion commands.
set -euo pipefail
export PATH=/usr/sbin:/usr/bin:/sbin:/bin
[[ $EUID == 0 && $# == 0 ]] || { echo 'Run this reviewed installer with sudo and no arguments.' >&2; exit 1; }
readonly SOURCE=/home/a/st-workbench-i210/axis-fault-management
readonly BINARY_SHA=ceae22f11a89623566d0d533737386cb131be29a11ebce52061336ef1e004b98
readonly HELPER_SHA=208390e794bf248c0ab0217ab3eb0c0a26151cc8509fa864ad56ded0aaefe8e7
readonly BINARY=/opt/st-workbench-ethercat/ecmotion-rt-ceae22f11a89
[[ ! -L $SOURCE/ecmotion-rt && ! -L $SOURCE/apply-runtime-hardware.py ]] || exit 1
echo "$BINARY_SHA  $SOURCE/ecmotion-rt" | sha256sum --check --status
echo "$HELPER_SHA  $SOURCE/apply-runtime-hardware.py" | sha256sum --check --status
for directory in /opt/st-workbench-ethercat /etc/st-workbench-ethercat /usr/local/libexec /var/lib/st-workbench-ethercat; do
    [[ ! -L $directory ]] || { echo "Refusing symbolic link: $directory" >&2; exit 1; }
done
install -d -o root -g a -m 750 /opt/st-workbench-ethercat /etc/st-workbench-ethercat /var/lib/st-workbench-ethercat
install -d -o root -g root -m 755 /usr/local/libexec
temporary_binary=$(mktemp /opt/st-workbench-ethercat/.binary.XXXXXX)
temporary_helper=$(mktemp /usr/local/libexec/.stworkbench-helper.XXXXXX)
temporary_config=$(mktemp /etc/st-workbench-ethercat/.installation.XXXXXX)
trap 'rm -f "$temporary_binary" "$temporary_helper" "$temporary_config"' EXIT
# Check the root-owned copies before publishing executable names or privileges.
install -o root -g a -m 750 "$SOURCE/ecmotion-rt" "$temporary_binary"
install -o root -g root -m 755 "$SOURCE/apply-runtime-hardware.py" "$temporary_helper"
echo "$BINARY_SHA  $temporary_binary" | sha256sum --check --status
echo "$HELPER_SHA  $temporary_helper" | sha256sum --check --status
printf '{"binary":"%s","binarySha256":"%s","allowInitialStart":true}\n' "$BINARY" "$BINARY_SHA" > "$temporary_config"
chmod 600 "$temporary_config"
mv -f "$temporary_binary" "$BINARY"
mv -f "$temporary_helper" /usr/local/libexec/stworkbench-ethercat-apply
mv -f "$temporary_config" /etc/st-workbench-ethercat/installation.json
echo 'Installed pinned generic core and fixed hardware-apply helper; no device/service action performed. The existing restricted hardware broker remains available; sudoers was not modified.'
