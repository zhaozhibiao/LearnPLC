#include "reference_history.hpp"

#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <stdexcept>

namespace {
void require(bool value, const char* message) {
    if (!value) { std::cerr << "FAIL: " << message << '\n'; std::exit(1); }
}
void near(long double actual, long double expected, const char* message) {
    require(std::fabs(actual - expected) < 0.000001L, message);
}
void queries_and_reverse() {
    csp_timing::ReferenceHistory history(4, 100);
    require(!history.position_at(100), "empty history invalid");
    require(history.publish(8, 100, -100), "first publication");
    near(*history.position_at(100), -100, "single exact sample");
    require(!history.position_at(101), "single sample cannot extrapolate");
    require(history.publish(9, 200, -200), "reverse publication");
    near(*history.position_at(150), -150, "signed reverse interpolation");
    require(history.publish(10, 300, -100), "direction reversal");
    near(*history.position_at(250), -150, "direction reversal interpolation");
    require(!history.position_at(99) && !history.position_at(301), "out of range invalid");
}
void invalid_capacity_and_gap() {
    for (std::size_t capacity : {std::size_t{0}, std::size_t{1}}) {
        bool threw = false;
        try { csp_timing::ReferenceHistory history(capacity, 100); }
        catch (const std::invalid_argument&) { threw = true; }
        require(threw, "capacity needs an interpolation bracket");
    }
    bool threw = false;
    try { csp_timing::ReferenceHistory history(2, 0); }
    catch (const std::invalid_argument&) { threw = true; }
    require(threw, "zero allowed gap rejected");
}
void rejects_and_latches() {
    for (int failure = 0; failure < 5; ++failure) {
        csp_timing::ReferenceHistory history(4, 100);
        require(history.publish(10, 100, 0), "fault setup");
        const bool accepted = failure == 0 ? history.publish(10, 200, 1) :
            failure == 1 ? history.publish(9, 200, 1) :
            failure == 2 ? history.publish(12, 200, 1) :
            failure == 3 ? history.publish(11, 100, 1) :
                           history.publish(11, 200, 1, false);
        require(!accepted && history.faulted(), "invalid publication latches fault");
        require(!history.publish(11, 200, 1), "later valid sample cannot silently recover");
        require(!history.position_at(100), "latched history cannot query old exact sample");
        history.reset();
        require(!history.faulted() && history.size() == 0, "explicit reset clears history and latch");
        require(history.publish(1, 1, 1), "reset allows new epoch");
    }
    csp_timing::ReferenceHistory sparse(3, 100);
    require(sparse.publish(1, 100, 1), "sparse setup");
    require(!sparse.publish(2, 201, 2), "sparse gap invalid");
    require(!sparse.position_at(150), "no interpolation through sparse gap");
    csp_timing::ReferenceHistory backwards(3, 100);
    require(backwards.publish(1, 200, 1), "timestamp setup");
    require(!backwards.publish(2, 199, 2), "decreasing timestamp invalid");
}
void ring_and_large_times() {
    csp_timing::ReferenceHistory history(3, 100);
    for (std::uint64_t i = 1; i <= 20; ++i)
        require(history.publish(i, i * 100, static_cast<std::int64_t>(i)), "ring publication");
    require(history.size() == 3, "ring capacity is bounded");
    require(!history.position_at(1700), "oldest overwritten");
    near(*history.position_at(1850), 18.5L, "ring wrap interpolation");
    csp_timing::ReferenceHistory large(3, 100);
    constexpr auto max = std::numeric_limits<std::uint64_t>::max();
    require(large.publish(max - 1, max - 100, -10), "large timestamp first");
    require(large.publish(max, max, 10), "large timestamp second");
    near(*large.position_at(max - 50), 0, "large timestamp interpolation avoids cancellation");
    require(!large.publish(0, 1, 0), "uint64 cycle/time wrap requires reset");
}
void explicit_delay_and_unmasked_actual() {
    csp_timing::ReferenceHistory history(8, 100);
    for (std::uint64_t i = 1; i <= 6; ++i)
        require(history.publish(i, i * 100, static_cast<std::int64_t>(i * 10)), "delay setup");
    csp_timing::DelayConfig config;
    auto result = history.evaluate(35, 500, 150, config);
    require(result.raw_actual_position == 35, "raw actual preserved");
    near(*result.raw_error, 25, "raw error uses newest actually published target");
    require(!result.aligned_error, "alignment disabled by default");
    config = {true, 150, 100, 200};
    result = history.evaluate(35, 500, 150, config);
    near(*result.aligned_reference, 35, "configured fractional cycle delay");
    near(*result.aligned_error, 0, "known delay aligns expected actual");
    near(*result.raw_error, 25, "alignment preserves raw error");
    require(!history.evaluate(35, 500, 99, config).aligned_error, "varying delay below bounds invalid");
    require(!history.evaluate(35, 500, 201, config).aligned_error, "varying delay above bounds invalid");
    require(!history.evaluate(35, 500, -1, config).aligned_error, "negative measured delay invalid");
    require(!history.evaluate(0, 100, 150, config).aligned_error, "timestamp subtraction underflow invalid");
    near(*history.evaluate(0, 500, 150, config).aligned_error, 35, "stopped actual remains real error");
    near(*history.evaluate(10, 500, 150, config).aligned_error, 25, "slow actual remains real error");
    require(!history.evaluate(35, 500, 150, {true, 250, 100, 200}).aligned_error,
            "configured delay outside bounds invalid");
    require(!history.evaluate(35, 500, 150, {true, 150, 200, 100}).aligned_error,
            "reversed bounds invalid");
    require(!history.evaluate(35, 500, 150, {true, -1, 0, 200}).aligned_error,
            "negative delay invalid");
    history.reset();
    require(!history.evaluate(1, 500, 150, config).raw_error, "reset has no invented raw reference");
}
}
int main() {
    invalid_capacity_and_gap();
    queries_and_reverse();
    rejects_and_latches();
    ring_and_large_times();
    explicit_delay_and_unmasked_actual();
    std::cout << "reference_history_selftest: PASS\n";
}
