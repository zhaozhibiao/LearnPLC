// SPDX-License-Identifier: GPL-3.0-or-later
// Separate compiler adapter; new feature path leaves legacy adapters byte-for-byte unchanged.
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { compile, parse, getVersion, discoverStlibs } from 'strucpp/node';
const args=process.argv.slice(2);
if(args.includes('--version')){console.log(`STruC++ version ${getVersion()}`);process.exit(0);}
try {
 if(getVersion()!=='0.7.0')throw new Error('Pinned compiler 0.7.0 required');
 const ri=args.indexOf('--request'),oi=args.indexOf('-o');if(ri<0||oi<0)throw new Error('Missing request/output');
 const request=JSON.parse(await readFile(args[ri+1],'utf8')),paths=args.slice(ri+2,oi);
 if(paths.length!==request.Sources.length)throw new Error('Source count mismatch');
 const sources=await Promise.all(paths.map(async fileName=>({fileName,source:await readFile(fileName,'utf8')})));
 for(let i=0;i<sources.length;i++){
  const parsed=parse(sources[i].source);
  if(parsed.errors.length){for(const e of parsed.errors)console.error(`${sources[i].fileName}:${e.line}:${e.column}: error: ${e.message.replaceAll('\n',' ')}`);process.exit(1);}
  if(parsed.ast.configurations.length)throw new Error('Tasks must come from project snapshot');
  const mapped=request.ProgramNames[request.Sources[i].ItemId];if(mapped&&!parsed.ast.programs.some(p=>p.name.toUpperCase()===mapped.toUpperCase()))throw new Error('Program mapping differs from source');
 }
 const tasks=request.Tasks.filter(t=>t.Enabled);if(tasks.length!==1)throw new Error('One cyclic task required');
 const task=tasks[0],name=request.ProgramNames[task.ProgramId];if(!name||!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)||!Number.isSafeInteger(task.IntervalMilliseconds)||task.IntervalMilliseconds<1)throw new Error('Invalid task');
 const config=`CONFIGURATION Config0\nRESOURCE Res0 ON PLC\nTASK TASK_${task.Name.replace(/[^A-Za-z0-9_]/g,'_').toUpperCase()}(INTERVAL := T#${task.IntervalMilliseconds}ms, PRIORITY := 0);\nPROGRAM Instance0 WITH TASK_${task.Name.replace(/[^A-Za-z0-9_]/g,'_').toUpperCase()} : ${name};\nEND_RESOURCE\nEND_CONFIGURATION\n`;
 const additional=[...sources.slice(1)];
 for(const key of ['AxisLibraryPath','AxisConstantsPath','MotionLibraryPath','ControlLibraryPath','CommunicationLibraryPath','CommunicationConstantsPath'])if(request[key])additional.push({fileName:'__workbench_'+key+'.st',source:await readFile(request[key],'utf8')});
 additional.push({fileName:'__workbench_configuration.st',source:config});
 const result=compile(sources[0].source,{fileName:sources[0].fileName,headerFileName:'generated.hpp',md5:request.ProgramMd5,additionalSources:additional,libraries:discoverStlibs(join(request.CompilerDistributionDirectory,'libs')).filter(l=>!request.AxisLibraryPath||l.manifest.name!=='plcopen-softmotion')});
 for(const d of [...result.errors,...result.warnings])console.error(`${d.file??sources[0].fileName}:${d.line}:${d.column}: ${d.severity}: ${d.message.replaceAll('\n',' ')}`);
 if(!result.success)process.exit(1);if(!result.debugTableCpp||!result.debugMap)throw new Error('Missing debug artifacts');
 const hooks=[];
 if(request.AxisLibraryPath)hooks.push(['axis_bridge.hpp','workbench_axis_after_scan']);
 if(request.MotionLibraryPath)hooks.push(['motion_bridge.hpp','workbench_motion_after_scan']);
 if(request.CommunicationLibraryPath)hooks.push(['communication_bridge.hpp','workbench_comm_after_scan']);
 let cpp=result.cppCode;
 if(hooks.length){const entry=`void Program_${name.toUpperCase()}::run() {`;if(cpp.split(entry).length!==2)throw new Error('Expected one scan entry');
  const guard=`\n struct WorkbenchScanGuard { int exceptions=std::uncaught_exceptions(); ~WorkbenchScanGuard(){${hooks.map(h=>h[1]+'(std::uncaught_exceptions()==exceptions);').join('')}} } workbenchScanGuard;\n`;
  cpp='#include <exception>\n'+hooks.map(h=>'#include "'+h[0]+'"\n').join('')+cpp.replace(entry,entry+guard);
 }
 const output=dirname(args[oi+1]);await writeFile(args[oi+1],cpp);await writeFile(join(output,'generated.hpp'),result.headerCode);await writeFile(join(output,'generated_debug.cpp'),result.debugTableCpp);await writeFile(join(output,'debug-map.json'),JSON.stringify(result.debugMap,null,2));await writeFile(join(output,'configuration.st'),config);
 console.log('Generated original control/communication library candidate with composed bounded scan hooks.');
}catch(e){console.error(`compiler-adapter:0:0: error: ${e.message}`);process.exit(1);}
