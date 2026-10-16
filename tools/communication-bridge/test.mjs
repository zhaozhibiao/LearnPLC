// SPDX-License-Identifier: MIT
import {readFile,writeFile,mkdir,cp} from 'node:fs/promises';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
const here=dirname(fileURLToPath(import.meta.url)),root=resolve(here,'../..');
const require=createRequire(resolve(root,'tools/compiler-adapter/package.json'));
const {compile,discoverStlibs}=require('strucpp/node');
const out=resolve(process.argv[2]??resolve(root,'artifacts/communication-st-test'));
await mkdir(out,{recursive:true});
const source=await readFile(resolve(here,'communication.st'),'utf8');
const result=compile(source,{fileName:'communication.st',headerFileName:'generated.hpp',libraries:discoverStlibs(resolve(root,'.tools/strucpp-0.7.0/strucpp/libs'))});
if(!result.success){console.error(JSON.stringify(result.errors,null,2));process.exit(1);}
await writeFile(resolve(out,'generated.cpp'),result.cppCode);await writeFile(resolve(out,'generated.hpp'),result.headerCode);
for(const name of ['test.cpp','generated_communication_scan_shim.cpp','communication_bridge.hpp'])await cp(resolve(here,name),resolve(out,name));
await cp(resolve(root,'.tools/strucpp-0.7.0/strucpp/runtime/include'),resolve(out,'include'),{recursive:true});
await cp(resolve(root,'.tools/strucpp-0.7.0/strucpp/COPYING.RUNTIME'),resolve(out,'COPYING.RUNTIME'));
console.log(`Generated actual communication ST + native hook harness in ${out}`);
