#!/usr/bin/env bash
# Every build uses a new directory so source mtime cannot bypass recompilation.
set -euo pipefail
umask 077
[[ $(id -u) != 0 ]] || { echo 'Build as an ordinary user' >&2; exit 1; }
[[ $# == 1 ]] || { echo 'Usage: build-native.sh OUTPUT-ROOT' >&2; exit 2; }
source_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
mkdir -p -- "$1"
build=$(mktemp -d "$(realpath -e -- "$1")/native-build-XXXXXXXX")
cmake_args=(-S "$source_directory" -B "$build" -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON)
if [[ -n ${ST_NATIVE_DEPENDENCY_ROOT:-} ]]; then cmake_args+=("-DCMAKE_PREFIX_PATH=$ST_NATIVE_DEPENDENCY_ROOT"); fi
cmake "${cmake_args[@]}"
cmake --build "$build" --clean-first --parallel 1
ctest --test-dir "$build" --timeout 20 --output-on-failure
mkdir -m 0700 -- "$build/core"
bash "$source_directory/build-native-core.sh" "$build/core" | tee "$build/native-core-build.log"
mapfile -t cores < <(find "$build/core" -type f -name plc_main)
[[ ${#cores[@]} == 1 ]] || { echo 'Expected exactly one independently built PLC core' >&2; exit 1; }
install -m 0700 -- "${cores[0]}" "$build/plc_main"
source_inventory=$(sha256sum "$source_directory/runtime-core/source-sha256.txt" | cut -d' ' -f1)
core_digest=$(sha256sum "$build/plc_main" | cut -d' ' -f1)
printf '{"schemaVersion":1,"runtime_commit":"a9e38d901fb87deb5a8cf778b52639b9387834d0","native_revision":"workbench-native-only-v1","source_inventory_sha256":"%s","elf_sha256":"%s"}\n' "$source_inventory" "$core_digest" > "$build/plc-core-receipt.json"
mkdir -m 0700 -- "$build/units"
cp -- "$source_directory"/units/* "$build/units/"
(
    cd "$build"
    for target in st-runtime-host st-engineering-gateway st-runtime-startup-gate st-session-helper st-deployment-coordinator st-motion-gateway st-axis-worker st-communication-worker st-path-worker st-axis-link-probe st-hardware-apply st-runtime-restore st-hardware-broker st-workbench-cpu-isolation; do
        [[ -x $target ]] || { echo "Required native target not built: $target" >&2; exit 1; }
        sha256sum -- "$target"
    done
    sha256sum plc_main plc-core-receipt.json
    for unit in units/*; do sha256sum -- "$unit"; done
) > "$build/native-binaries.sha256"
echo "Verified build: $build"
sha256sum -- "$build/native-binaries.sha256"
