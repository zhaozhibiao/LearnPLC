#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
# Compatibility entry point for the native-only installer.
set -euo pipefail
script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
exec bash "$script_directory/../native/install-root.sh" "$@"
