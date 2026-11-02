#!/bin/sh
# Local-only verifier. No controller service or network endpoint is used.
set -eu
cd "$(dirname "$0")/../.."
node tools/axis-bridge/test_contract.mjs
node tools/axis-bridge/compile_fixture.mjs
cxx=${CXX:-g++}
runtime=.tools/strucpp-0.7.0/strucpp/runtime/include
out=tools/axis-bridge/build
"$cxx" -std=c++20 -pthread -Itools/axis-bridge -I"$out" -I"$runtime" "$out/generated.cpp" tools/axis-bridge/generated_axis_scan_shim.cpp tools/axis-bridge/test_scan.cpp tools/axis-bridge/test_stub.cpp -o "$out/test_scan"
timeout 10 "$out/test_scan"
"$cxx" -std=c++20 -pthread -Itools/axis-bridge -I"$out" -I"$runtime" "$out/generated.cpp" tools/axis-bridge/generated_axis_scan_shim.cpp tools/axis-bridge/test_public_interfaces.cpp tools/axis-bridge/test_stub.cpp -o "$out/test_public_interfaces"
python3 tools/axis-bridge/verify_public_interfaces.py "$out" "$out/test_public_interfaces"
"$cxx" -std=c++20 -Wall -Wextra -Werror -pthread -Itools/axis-bridge tools/axis-bridge/axis_bridge.cpp tools/axis-bridge/test_transport.cpp -o "$out/test_transport"
timeout 10 "$out/test_transport"
