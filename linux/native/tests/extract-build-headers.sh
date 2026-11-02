#!/usr/bin/env bash
# Extract development headers into staging; never install system packages.
set -euo pipefail
cd /home/a/st-workbench-i210/linux-cpp-20261102/deps
mkdir -p sysroot
for package in libboost1.83-dev_*.deb libssl-dev_*.deb libarchive-dev_*.deb; do
    dpkg-deb -x "$package" sysroot
done
test -f sysroot/usr/include/boost/beast.hpp
test -f sysroot/usr/include/openssl/ssl.h
test -f sysroot/usr/include/archive.h
echo 'Private C++ build headers ready'
