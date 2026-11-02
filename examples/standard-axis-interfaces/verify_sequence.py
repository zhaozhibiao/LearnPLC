#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
import pathlib,subprocess,sys,tempfile
output=pathlib.Path(sys.argv[1]).resolve(strict=True)
cases=('cold','sequence','offset','pulse','unhomed','limits','overspeed','cancel','disconnect')
with tempfile.TemporaryDirectory(prefix='standard-interface-sequence-') as folder:
 binary=str(pathlib.Path(folder)/'sequence')
 subprocess.run(['g++','-std=c++20','-I'+str(output),'-I'+str(output/'strucpp_runtime/include'),str(output/'generated.cpp'),str(pathlib.Path(__file__).with_name('test_sequence.cpp')),'-o',binary],check=True)
 for case in cases:subprocess.run([binary,case],check=True)
print('PASS 9 concise-sequence scenarios; physical_motion_tested=false')
