# External GPL compiler adapter

This command-line helper is GPL-3.0-or-later. It runs as a separate Node process and imports the official STruCpp 0.7.0 compiler API. The desktop and .NET toolchain do not import or copy compiler internals. The helper delegates parsing, type analysis, C++ generation, debug table generation, and symbol mapping to the upstream compiler.

Install with `npm ci --prefix tools/compiler-adapter --ignore-scripts`. `package-lock.json` pins the official release archive and dependency integrity. The npm registry does not publish this compiler; the dependency URL is the official GitHub v0.7.0 release archive. Node 22 or newer is required by the compiler.

`compile.mjs --version` reports the actual compiler identity. Normal invocation is `node compile.mjs --request request.json input.st [other.st ...] -o generated.cpp`. The request contains immutable source identities, task configuration, explicit item-ID-to-PROGRAM-name mapping, program MD5, and the compiler distribution directory. The upstream parser validates the mapping and rejects existing source CONFIGURATION declarations. One enabled cyclic task is supported; its exact millisecond interval is emitted under Config0/Res0/Instance0, matching the pinned Runtime shim. Compiler errors are returned through stderr with file/line/column and a nonzero exit code.

The compiler API's `debugTableCpp` and `debugMap` are required. The native standalone CLI omits these artifacts even with `--debug`; it cannot supply this Runtime package by itself. This helper writes the original compiler outputs without inventing debug pointers or ABI tables.

Upstream compiler source is saved locally at `.tools/strucpp-source-v0.7.0.zip`, SHA256 `9A6C15B366E33AF5390E4BDCD8F4462809101926E958657ABEFB73BB115D8342`, from `https://github.com/Autonomy-Logic/STruCpp/archive/refs/tags/v0.7.0.zip`. `LICENSE` and `UPSTREAM-COPYING` preserve original notices. The complete helper source is this directory. No shipping approval is implied; corresponding-source delivery for a distributed product must include the compiler's complete source and dependencies, notices, and build information.

## Local Runtime candidate boundary

The .NET PackageBuilder creates a local isolated Runtime staging candidate ZIP pinned to Runtime 4.3.0 commit `a9e38d901fb87deb5a8cf778b52639b9387834d0`. It includes generated source/header/debug table/map, the exact task configuration, `defines.h` with `PROGRAM_MD5`, original runtime headers and license texts, and an inventory manifest with source/hardware/compiler/task/resource/symbol identities. This is a candidate for isolated Linux compilation, not a safely downloadable active-device package. Native upload stops the active PLC and replaces the compiled library; direct upload remains disabled.

The manifest explicitly sets `SafeForDirectUpload=false`, `RuntimeLinkVerified=false`, and `CommercialRedistributionCleared=false`. Original `iec_fault.hpp` and `iec_global.hpp` have no individual runtime-exception license notices, although upstream COPYING assigns the exception to runtime headers as a category. COPYING.RUNTIME applies to files bearing the notice. No notice is added or altered; this discrepancy requires authoritative clarification before commercial redistribution. All copied files retain their original contents and are hashed in the manifest.

Run the real package tests by setting `ST_COMPILER_ADAPTER` to the absolute `compile.mjs` path and `ST_COMPILER_DISTRIBUTION` to the official extracted compiler directory, then running `dotnet run --project tests/StWorkbench.Toolchain.Tests -c Release`. Optional `ST_COMPILER_PATH` enables the native CLI smoke tests too.
