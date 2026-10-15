// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ST Workbench contributors
// Independently implemented from the documented public interface.
#pragma once
#include <type_traits>
#include <utility>
#ifdef STRUCPP_THREADED
#include <mutex>
#endif

namespace strucpp {
// The public value member is part of the generated-code interface. Concurrent
// callers must use read/write/with_lock rather than access it directly.
template<class V>
class GlobalVar {
public:
    V value;

    GlobalVar() : value{} {}
    template<class T, typename std::enable_if<
        !std::is_same<typename std::decay<T>::type, GlobalVar>::value, int>::type = 0>
    explicit GlobalVar(T&& initial) : value(std::forward<T>(initial)) {}
    GlobalVar(const GlobalVar&) = delete;
    GlobalVar& operator=(const GlobalVar&) = delete;

    // Materialize the effective value in an independent, unforced IECVar.
    V read() const {
#ifdef STRUCPP_THREADED
        std::lock_guard<std::mutex> guard(mutex_);
#endif
        return V(value.get());
    }

    template<class T>
    void write(T&& input) {
#ifdef STRUCPP_THREADED
        std::lock_guard<std::mutex> guard(mutex_);
#endif
        value.set(std::forward<T>(input));
    }

    // The callback runs while holding this object's lock. It must not reenter
    // the same GlobalVar, or retain its pointer for concurrent access later.
    template<class F>
    auto with_lock(F&& callback) -> decltype(std::forward<F>(callback)(&value)) {
#ifdef STRUCPP_THREADED
        std::lock_guard<std::mutex> guard(mutex_);
#endif
        return std::forward<F>(callback)(&value);
    }

private:
#ifdef STRUCPP_THREADED
    mutable std::mutex mutex_;
#endif
};
}
