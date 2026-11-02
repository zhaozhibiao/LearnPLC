#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Run compiler-generated ST in memory; no sockets, device IO or axis service."""
import pathlib,subprocess,sys,tempfile,os
output=pathlib.Path(sys.argv[1]).resolve(strict=True)
cases=('cold','sequence','offset','unhomed','limits','feedback-loss','cancel','moving','stop-feedback-loss','stop-error','speed30','speed60','overspeed')
with tempfile.TemporaryDirectory(prefix='standard-axis-tests-') as folder:
 binary=str(pathlib.Path(folder)/'sequence')
 subprocess.run([os.environ.get('CXX','g++'),'-std=c++20','-I'+str(output),'-I'+str(output/'strucpp_runtime/include'),str(output/'generated.cpp'),str(pathlib.Path(__file__).with_name('test_sequence.cpp')),'-o',binary],check=True)
 for case in cases:subprocess.run([binary,case],check=True)
print('PASS memory-only standard-axis sequence: '+str(len(cases))+' scenarios; physical_motion_tested=false')
