#!/usr/bin/env bash
# Prepared source installation; must be explicitly executed by the operator.
set -euo pipefail
umask 077
readonly revision=a9e38d901fb87deb5a8cf778b52639b9387834d0
readonly bundle_hash=4ea4e8afe6f8084688f2157cfc87998d4159d55226055cc9f1d9fa559df8b06c
readonly prefix="$HOME/.local/share/st-workbench/runtime-v4.3.0"
readonly src="$prefix/source"
readonly deps="$prefix/dependencies"
readonly scripts_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
resume=false
if [[ "${1:-}" == --resume && "$#" == 1 ]]; then resume=true
elif [[ "$#" != 0 ]]; then echo 'Usage: install-user.sh [--resume]' >&2; exit 1; fi
if [[ "$EUID" == 0 || "$HOME" != /home/a ]]; then
    echo 'This reviewed installation targets only unprivileged user a in /home/a.' >&2; exit 1
fi
if [[ -e "$prefix" && "$resume" != true ]]; then echo 'Installation directory already exists; review progress and use --resume explicitly.' >&2; exit 1; fi
if [[ -L "$prefix" || -L "$src" || -L "$deps" ]]; then echo 'Installation paths may not be symlinks.' >&2; exit 1; fi
[[ "$(realpath -m "$prefix")" == /home/a/.local/share/st-workbench/runtime-v4.3.0 ]] || { echo 'Resolved installation path is outside the reviewed directory.' >&2; exit 1; }
if systemctl --user is-active --quiet stworkbench-openplc.service; then echo 'Runtime service is active; refusing installation changes.' >&2; exit 1; fi
if compgen -G "$src/build/libplc_*.so" >/dev/null || [[ -f "$src/build/new_libplc.so" ]]; then
    echo 'Existing program library requires explicit preservation and activation review.' >&2; exit 1
fi
for tool in git curl gcc g++ cmake make pkg-config dpkg-deb python3 sha256sum openssl flock; do
    command -v "$tool" >/dev/null || { echo "Required tool missing: $tool" >&2; exit 1; }
done
[[ "$(dpkg-query -W -f='${Version}' libpython3.13:amd64)" == 3.13.5-2+deb13u5 ]] || {
    echo 'Python revision changed; dependency package pin must be reviewed.' >&2; exit 1;
}
mkdir -p "$prefix/downloads" "$deps" "$prefix/run" "$prefix/data"
[[ ! -L "$prefix/install.lock" ]] || { echo 'Install lock may not be a symbolic link.' >&2; exit 1; }
exec 9>"$prefix/install.lock"
flock --nonblock 9 || { echo 'Another installer owns this Runtime prefix.' >&2; exit 1; }
cd "$prefix/downloads"
# Debian packages are only extracted under this prefix, never installed with dpkg/apt.
download_package() {
    local package_url="$1" package_file="$2" expected_hash="$3"
    if [[ -f "$package_file" ]] && printf '%s  %s\n' "$expected_hash" "$package_file" | sha256sum --check --status; then
        echo "Verified cache: $package_file"; return
    fi
    echo "Downloading dependency: $package_file"
    curl --silent --show-error --fail --location --proto '=https' --tlsv1.2 \
      --connect-timeout 15 --max-time 1800 --continue-at - "$package_url" -o "$package_file"
    printf '%s  %s\n' "$expected_hash" "$package_file" | sha256sum --check --strict
}
download_package \
  'https://deb.debian.org/debian/pool/main/p/python3.13/libpython3.13-dev_3.13.5-2+deb13u5_amd64.deb' python-dev.deb \
  4bd7b17ffbeb367ce1fef8b6ff02536a25f29fc07372d1bdd3ed1e1faa5b9409
download_package \
  'https://deb.debian.org/debian/pool/main/p/python-pip/python3-pip-whl_25.1.1+dfsg-1_all.deb' pip.deb \
  e65351935aa4af2b10ae2abb5744f5059e9abad51aea6cd0cb825702010763f7
printf '%s\n' \
  '4bd7b17ffbeb367ce1fef8b6ff02536a25f29fc07372d1bdd3ed1e1faa5b9409  python-dev.deb' \
  'e65351935aa4af2b10ae2abb5744f5059e9abad51aea6cd0cb825702010763f7  pip.deb' | sha256sum --check --strict
dpkg-deb --extract python-dev.deb "$deps"
dpkg-deb --extract pip.deb "$deps"
# Use the installed, matching shared Python library with the extracted headers.
if [[ -L "$deps/usr/lib/x86_64-linux-gnu/libpython3.13.so.1" ]]; then
    [[ "$(readlink "$deps/usr/lib/x86_64-linux-gnu/libpython3.13.so.1")" == /usr/lib/x86_64-linux-gnu/libpython3.13.so.1.0 ]] || exit 1
else
    ln -s /usr/lib/x86_64-linux-gnu/libpython3.13.so.1.0 "$deps/usr/lib/x86_64-linux-gnu/libpython3.13.so.1"
fi
mkdir -p "$prefix/pkgconfig"
cat > "$prefix/pkgconfig/python3-embed.pc" <<EOF
prefix=$deps/usr
libdir=\${prefix}/lib/x86_64-linux-gnu
includedir=\${prefix}/include
Name: Python
Description: Reviewed user-local Python 3.13 embedding headers
Version: 3.13
Libs: -L\${libdir} -lpython3.13
Cflags: -I\${includedir}/python3.13 -I\${includedir}
EOF
export PKG_CONFIG_PATH="$prefix/pkgconfig"
source_bundle="$prefix/downloads/runtime-v4.3.0-source.tar.gz"
if [[ -f "$source_bundle" && "$(git -C "$src" rev-parse HEAD 2>/dev/null || true)" != "$revision" ]]; then
    printf '%s  %s\n' "$bundle_hash" "$source_bundle" | sha256sum --check --strict
    if git -C "$src" rev-parse --verify HEAD >/dev/null 2>&1; then echo 'Existing source HEAD differs from the pinned revision; refusing replacement.' >&2; exit 1; fi
    if [[ -d "$src" ]] && find "$src" -mindepth 1 -maxdepth 1 ! -name .git -print -quit | grep -q .; then
        echo 'Uncommitted source files exist; refusing source bundle extraction.' >&2; exit 1
    fi
    mkdir -p "$src"
    tar -xzf "$source_bundle" -C "$src"
    [[ "$(git -C "$src" rev-parse HEAD)" == "$revision" ]] || exit 1
fi
git init "$src"
if git -C "$src" remote get-url origin >/dev/null 2>&1; then
    [[ "$(git -C "$src" remote get-url origin)" == https://github.com/Autonomy-Logic/openplc-runtime.git ]] || { echo 'Unexpected Git origin.' >&2; exit 1; }
else
    git -C "$src" remote add origin https://github.com/Autonomy-Logic/openplc-runtime.git
fi
if [[ "$(git -C "$src" rev-parse HEAD 2>/dev/null || true)" != "$revision" ]]; then
    git -C "$src" -c http.lowSpeedLimit=1024 -c http.lowSpeedTime=30 fetch --depth 1 origin "$revision"
    git -C "$src" checkout --detach FETCH_HEAD
fi
[[ "$(git -C "$src" rev-parse HEAD)" == "$revision" ]] || exit 1
python3 "$scripts_dir/isolate.py" "$src" "$prefix/run"
git -C "$src" diff --binary > "$prefix/isolation.patch"
if [[ ! -x "$src/venvs/runtime/bin/python3" ]]; then python3 -m venv --without-pip "$src/venvs/runtime"; fi
pip_wheels=("$deps"/usr/share/python-wheels/pip-*.whl)
[[ "${#pip_wheels[@]}" == 1 && -f "${pip_wheels[0]}" ]] || { echo 'Expected exactly one extracted pip wheel.' >&2; exit 1; }
PYTHONPATH="${pip_wheels[0]}" "$src/venvs/runtime/bin/python3" -m pip install --no-index "${pip_wheels[0]}"
"$src/venvs/runtime/bin/python3" -m pip install --disable-pip-version-check --progress-bar off --timeout 30 --retries 2 --only-binary=:all: -r "$src/requirements.txt"
"$src/venvs/runtime/bin/python3" -m pip freeze --all > "$prefix/python-lock.txt"
# No plugins, EtherDOG, SOEM, or C/C++ program library are installed.
printf '# No hardware plugins enabled in the isolated ST runtime.\n' > "$src/plugins.conf"
cmake -S "$src" -B "$src/build" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_EXE_LINKER_FLAGS="-L$deps/usr/lib/x86_64-linux-gnu"
cmake --build "$src/build" --parallel 2
test -x "$src/build/plc_main"
ldd "$src/build/plc_main" > "$prefix/native-libraries.txt"
if grep -q 'not found' "$prefix/native-libraries.txt"; then echo 'Unresolved native library.' >&2; exit 1; fi
# Regenerate any checked-in TLS files: private key never leaves this device.
if [[ -f "$prefix/tls-public.sha256" ]]; then
    (cd "$src/webserver" && sha256sum --check --strict "$prefix/tls-public.sha256")
    test -f "$src/webserver/keyOPENPLC.pem"
else
  openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 365 \
  -keyout "$src/webserver/keyOPENPLC.pem" -out "$src/webserver/certOPENPLC.pem" \
  -subj '/CN=localhost' -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
  -addext 'basicConstraints=critical,CA:TRUE'
  (cd "$src/webserver" && sha256sum certOPENPLC.pem > "$prefix/tls-public.sha256")
fi
chmod 600 "$src/webserver/keyOPENPLC.pem"
cp "$scripts_dir/run-user.sh" "$prefix/run-user.sh"
cp "$scripts_dir/launcher-gate.py" "$prefix/launcher-gate.py"
chmod 700 "$prefix/run-user.sh"
mkdir -p "$HOME/.config/systemd/user"
if [[ -e "$HOME/.config/systemd/user/stworkbench-openplc.service" ]]; then
    cmp --silent "$scripts_dir/stworkbench-openplc.service" "$HOME/.config/systemd/user/stworkbench-openplc.service" || { echo 'User unit differs; refusing overwrite.' >&2; exit 1; }
else
    cp "$scripts_dir/stworkbench-openplc.service" "$HOME/.config/systemd/user/"
fi
printf '%s\n' "$revision" > "$prefix/upstream-commit.txt"
echo 'Prepared only. Service is not enabled or started; existing PLC and axis services were not changed.'
