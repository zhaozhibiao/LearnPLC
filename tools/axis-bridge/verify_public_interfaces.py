#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
import pathlib,subprocess,sys,tempfile
root=pathlib.Path(sys.argv[1]).resolve()
cases=('active-ack','new-error-clear','terminal-retained','terminal-reset','early-release','busy-retrigger','aborted-reset','stop-held','reader-command-error','reader-axis-fault','reader-link-loss','legacy-worker-failure','reader-enable','reader-stop-lock','power-live','power-fault','power-pending-enable','power-pending-error','velocity-correlated')
with tempfile.TemporaryDirectory(prefix='axis-public-contract-') as folder:
 binary=str(pathlib.Path(sys.argv[2]).resolve()) if len(sys.argv)>2 else str(pathlib.Path(folder)/'contract')
 if len(sys.argv)<=2:
  subprocess.run(['g++','-std=c++20','-pthread','-I'+str(root),'-I'+str(root/'strucpp_runtime/include'),str(root/'generated.cpp'),str(root/'generated_axis_scan_shim.cpp'),str(root/'test_stub.cpp'),str(root/'test_public_interfaces.cpp'),'-o',binary],check=True)
 failures=[]
 for case in cases:
  if subprocess.run([binary,case]).returncode:failures.append(case)
 if failures:raise SystemExit('FAILED: '+', '.join(failures))
 print('PASS '+str(len(cases))+' public interface scenarios; no hardware access')
