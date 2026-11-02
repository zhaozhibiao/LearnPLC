#!/usr/bin/env python3
"""MIT. Compile the real MCP build output, then run memory-only ST scenarios.

Usage: python3 verify_sequence.py /path/to/mcp-build/output
No EtherCAT access, sockets, sudo or motion controller are used.
"""
import pathlib
import subprocess
import sys
import tempfile

SCENARIOS = ("cold", "unconfirmed", "unhomed", "offline", "distance",
             "nan-distance", "nan-velocity", "nan-tolerance", "moving",
             "cancel-before-start", "roundtrip", "home-roundtrip", "home-failure",
             "cancel", "feedback-loss", "timeout")


def main():
    output = pathlib.Path(sys.argv[1]).resolve(strict=True)
    source = pathlib.Path(__file__).with_name("test_sequence.cpp").resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="is620n-st-test-") as directory:
        binary = str(pathlib.Path(directory) / "test-sequence")
        subprocess.run(["g++", "-std=c++20", "-I" + str(output),
                        "-I" + str(output / "strucpp_runtime/include"),
                        str(output / "generated.cpp"), str(source), "-o", binary], check=True)
        for scenario in SCENARIOS:
            subprocess.run([binary, scenario], check=True)
    print(f"ST_SEQUENCE_TESTS_PASS count={len(SCENARIOS)} physical_motion_tested=false", flush=True)


if __name__ == "__main__":
    main()
