import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {pathToFileURL} from 'node:url';
import {createRequire} from 'node:module';
const require = createRequire(new URL('../compiler-adapter/package.json', import.meta.url));
const {compile, getVersion} = await import(pathToFileURL(require.resolve('strucpp/node')));
if(getVersion() !== '0.7.0') throw Error('Pinned compiler required');
const root = new URL('.',import.meta.url), out = new URL('build/',root);
await mkdir(out,{recursive:true});
const source = await readFile(new URL('scan_fixture.st',root),'utf8');
const result=compile(source,{fileName:'scan_fixture.st',headerFileName:'generated.hpp',additionalSources:[{fileName:'axis_control.st',source:await readFile(new URL('axis_control.st',root),'utf8')}]});
for(const d of [...result.errors,...result.warnings])console.error(d);
if(!result.success)process.exit(1);
const entry='void Program_AXISFIXTURE::run() {';
if(result.cppCode.split(entry).length!==2)throw Error('Expected one fixture run entry');
const guard='\nstruct AxisScanGuard { int exceptions = std::uncaught_exceptions(); ~AxisScanGuard() { workbench_axis_after_scan(std::uncaught_exceptions() == exceptions); } } axisScanGuard;\nif (axis_fixture_throw) throw 1;\n';
await writeFile(new URL('generated.cpp',out),'#include <exception>\nextern bool axis_fixture_throw;\n#include "axis_bridge.hpp"\n'+result.cppCode.replace(entry,entry+guard));
await writeFile(new URL('generated.hpp',out),result.headerCode);
console.log('Pinned ST compiler generated axis fixture successfully');


