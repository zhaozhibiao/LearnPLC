#!/usr/bin/env bash
set -euo pipefail
cd /home/a/st-workbench-i210/linux-cpp-20261102
./st-runtime-host --no-spawn --port 19443 > runtime-isolated.log 2>&1 &
host=$!
./st-engineering-gateway --port 19445 > gateway-isolated.log 2>&1 &
gateway=$!
trap 'kill "$host" "$gateway" 2>/dev/null || true; wait "$host" "$gateway" 2>/dev/null || true' EXIT
sleep 1
kill -0 "$host" "$gateway"
timeout 20 ./runtime-http-integration-test runtime 19443
status=0
timeout 20 ./runtime-http-integration-test gateway 19445 || status=$?
echo "gateway_integration_exit=$status"
exit "$status"
