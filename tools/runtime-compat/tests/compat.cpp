#include "iec_global.hpp"
#include "iec_fault.hpp"
#include "iec_var.hpp"
#include <cassert>
#include <type_traits>
#include <thread>
#include <vector>
#include <iostream>

using Variable = strucpp::IECVar<int>;
using Global = strucpp::GlobalVar<Variable>;
static_assert(!std::is_copy_constructible<Global>::value, "Global identity cannot be copied");
static_assert(!std::is_copy_assignable<Global>::value, "Global identity cannot be assigned");
static_assert(static_cast<unsigned>(strucpp::IecFault::NullReference) == 0, "fault ABI");
static_assert(static_cast<unsigned>(strucpp::IecFault::ArrayBounds) == 1, "fault ABI");
static_assert(static_cast<unsigned>(strucpp::IecFault::BadLocation) == 2, "fault ABI");
#ifdef EXPECT_NO_EXCEPTIONS
static_assert(STRUCPP_HAS_EXCEPTIONS == 0, "exceptions disabled");
#else
static_assert(STRUCPP_HAS_EXCEPTIONS == 1, "exceptions enabled");
#endif
#ifndef STRUCPP_THREADED
static_assert(sizeof(Global) == sizeof(Variable), "single-thread storage has no mutex");
#endif
int main() {
    Global zero;
    assert(zero.read().get() == 0);
    Global global(4);
    global.write(7);
    assert(global.read().get() == 7);
    global.with_lock([](Variable* value) { value->force(19); });
    global.write(8);
    Variable snapshot = global.read();
    assert(snapshot.get() == 19 && !snapshot.is_forced());
    snapshot.set(21);
    assert(global.read().get() == 19);
    global.with_lock([](Variable* value) { value->unforce(); });
    global.write(8);
    assert(global.read().get() == 8);
    Variable& reference = global.with_lock([](Variable* value) -> Variable& { return *value; });
    assert(&reference == &global.value);
    Global copied_initial(Variable(3));
    assert(copied_initial.read().get() == 3);
#ifdef STRUCPP_THREADED
    Global counter;
    std::vector<std::thread> workers;
    for (int thread = 0; thread < 4; ++thread)
        workers.emplace_back([&] { for (int iteration = 0; iteration < 20000; ++iteration)
            counter.with_lock([](Variable* value) { value->set(value->get() + 1); }); });
    for (auto& worker : workers) worker.join();
    assert(counter.read().get() == 80000);
#endif
    std::cout << "compatibility contract passed\n";
}
