// SPDX-License-Identifier: MIT
// Generates the actual ST library with the pinned compiler; never substitutes a model.
import {readFile,writeFile,mkdir,cp} from 'node:fs/promises';
import {resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createRequire} from 'node:module';
const here=dirname(fileURLToPath(import.meta.url));
const root=resolve(here,'../..');
const require=createRequire(resolve(root,'tools/compiler-adapter/package.json'));
const {compile,discoverStlibs}=require('strucpp/node');
const out=resolve(process.argv[2]??resolve(root,'artifacts/control-library-test'));
await mkdir(out,{recursive:true});
const source=await readFile(resolve(here,'control.st'),'utf8');
const result=compile(source,{fileName:'control.st',headerFileName:'generated.hpp',libraries:discoverStlibs(resolve(root,'.tools/strucpp-0.7.0/strucpp/libs'))});
if(!result.success) { console.error(JSON.stringify(result.errors,null,2));process.exit(1); }
await writeFile(resolve(out,'generated.cpp'),result.cppCode);
await writeFile(resolve(out,'generated.hpp'),result.headerCode);
await cp(resolve(here,'test.cpp'),resolve(out,'test.cpp'));
await cp(resolve(root,'.tools/strucpp-0.7.0/strucpp/runtime/include'),resolve(out,'include'),{recursive:true});
await cp(resolve(root,'.tools/strucpp-0.7.0/strucpp/COPYING.RUNTIME'),resolve(out,'COPYING.RUNTIME'));
await cp(resolve(root,'.tools/strucpp-0.7.0/strucpp/COPYING'),resolve(out,'COPYING.COMPILER'));
console.log(`Actual ST generated into ${out}; compile test.cpp and generated.cpp with C++17 and -Iinclude`);
