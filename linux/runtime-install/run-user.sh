#!/usr/bin/env bash
set -euo pipefail
umask 077
readonly prefix="$HOME/.local/share/st-workbench/runtime-v4.3.0"
export OPENPLC_RUNTIME_DIR="$prefix/run"
export OPENPLC_PERSISTENT_DATA_DIR="$prefix/data"
mkdir -p "$OPENPLC_RUNTIME_DIR" "$OPENPLC_PERSISTENT_DATA_DIR"
cd "$prefix/source"
test -x build/plc_main
# Exact committed identity only; pending transactions and unknown libraries refuse startup.
python3 "$prefix/launcher-gate.py"
exec venvs/runtime/bin/python3 -m webserver.app
