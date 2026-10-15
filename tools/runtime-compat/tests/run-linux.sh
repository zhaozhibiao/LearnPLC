#!/usr/bin/env bash
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
upstream=${1:?Pass permitted IECVar include directory}
out=$(mktemp -d)
trap 'rm -f -- "$out/test"; rmdir -- "$out"' EXIT
for standard in c++14 c++17; do
  for mode in plain threaded noexceptions; do
    flags=()
    case "$mode" in
      threaded) flags=(-DSTRUCPP_THREADED);;
      noexceptions) flags=(-fno-exceptions -DEXPECT_NO_EXCEPTIONS);;
    esac
    g++ -std="$standard" "${flags[@]}" -I"$root/include" -I"$upstream" "$root/tests/compat.cpp" -pthread -o "$out/test"
    "$out/test"
  done
done
