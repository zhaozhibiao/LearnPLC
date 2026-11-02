#!/usr/bin/env bash
# Build a new PLC executable without an embedded interpreter, never touch the live build.
set -euo pipefail
umask 077
[[ ( $# == 1 || $# == 2 ) && $(id -u) != 0 ]] || { echo 'Usage as ordinary user: build-native-core.sh [REVIEWED-RUNTIME-SOURCE] OUTPUT-ROOT' >&2; exit 2; }
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
overlay=$script_directory/runtime-core-overlay
if [[ $# == 1 ]]; then
    source_directory=$script_directory/runtime-core
    output=$1
    (cd "$source_directory" && sha256sum --strict -c source-sha256.txt)
else
    source_directory=$(realpath -e -- "$1")
    output=$2
    (cd "$source_directory" && sha256sum --strict -c "$overlay/original-sha256.txt")
fi
[[ -f $source_directory/CMakeLists.txt && -d $source_directory/core && ! -L $source_directory/core ]] || exit 1
mkdir -p -- "$output"
stage=$(mktemp -d "$(realpath -e -- "$output")/native-plc-build-XXXXXXXX")
mkdir -- "$stage/source"
cp -a -- "$source_directory/core" "$stage/source/"
cp -- "$source_directory/CMakeLists.txt" "$stage/source/"
cp -- "$source_directory/LICENSE" "$stage/source/LICENSE"
if [[ $# == 2 ]]; then
    cp -a -- "$overlay/core/." "$stage/source/core/"
    cp -- "$overlay/native_plugin_selftest.c" "$stage/source/core/src/native_plugin_selftest.c"
    printf '\ninclude(CTest)\n' >> "$stage/source/CMakeLists.txt"
fi
cmake -S "$stage/source" -B "$stage/build" -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=ON
cmake --build "$stage/build" --clean-first --parallel 1
ctest --test-dir "$stage/build" --timeout 20 --output-on-failure
ldd "$stage/build/plc_main" > "$stage/linked-libraries.txt"
if grep -qi 'python' "$stage/linked-libraries.txt"; then echo 'Embedded interpreter remains linked' >&2; exit 1; fi
if readelf --wide -Ws "$stage/build/plc_main" | grep -Eq ' UND .*\bPy[A-Z_]'; then echo 'Unresolved interpreter API remains' >&2; exit 1; fi
sha256sum -- "$stage/build/plc_main"
echo "Native PLC build verified: $stage/build/plc_main"
