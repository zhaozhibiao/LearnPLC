# Independent Runtime compatibility declarations

The two MIT headers are independently written from the supplied public API contract. The original implementations of `iec_global.hpp` and `iec_fault.hpp` were not read or copied. `iec_var.hpp`'s explicitly exception-marked public interface was inspected to verify effective-value reads, forcing, and detached snapshot semantics. This records implementation provenance, not a legal clearance opinion for the entire upstream package.

`GlobalVar` protects read, write, and callbacks with one per-object mutex when `STRUCPP_THREADED` is defined. Callbacks must not reenter the same instance. Its public `value` field exists for generated code compatibility; direct concurrent access bypasses protection. Returned references must not be retained for unprotected concurrent access. The fault header declares the runtime-owned failure function and numeric fault ABI; it supplies no failure-function implementation.

Linux GCC14.2 contract tests passed in C++14/C++17, ordinary/threaded/exception-disabled variants. Tests cover forced effective values, independent unforced snapshots, write semantics, exact callback reference/void results, deleted copying, no single-thread mutex storage, and four threads performing80000 total protected increments. Missing headers first produced the expected failing build before implementation.

Run `tests/run-linux.sh /path/to/permitted/upstream/include` to reproduce. This directory's LICENSE applies only to these independent files; packaging must retain every other dependency's original license.
