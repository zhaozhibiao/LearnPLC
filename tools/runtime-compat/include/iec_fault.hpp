// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ST Workbench contributors
// Independently implemented from the documented public interface.
#pragma once
#include <cstdint>

#ifndef STRUCPP_HAS_EXCEPTIONS
#if defined(__cpp_exceptions) || defined(__EXCEPTIONS) || defined(_CPPUNWIND)
#define STRUCPP_HAS_EXCEPTIONS 1
#else
#define STRUCPP_HAS_EXCEPTIONS 0
#endif
#endif

namespace strucpp {
enum class IecFault : std::uint8_t {
    NullReference = 0,
    ArrayBounds = 1,
    BadLocation = 2
};
[[noreturn]] void iec_runtime_fault(IecFault fault, const char* context = nullptr) noexcept;
}
