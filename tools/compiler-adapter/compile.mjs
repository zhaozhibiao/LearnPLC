// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 ST Workbench contributors
// Separate GPL command-line tool; no compiler code is linked into the desktop application.
import { readFile, writeFile } from 'node:fs/promises';
import { dirname, join, basename } from 'node:path';
import { compile, parse, getVersion, discoverStlibs } from 'strucpp/node';

const args = process.argv.slice(2);
if (args.includes('--version')) {
  console.log(`STruC++ version ${getVersion()}`);
  process.exit(0);
}
try {
  if (getVersion() !== '0.7.0') throw new Error('Unsupported compiler version; this adapter requires 0.7.0');
  const requestIndex = args.indexOf('--request');
  const outputIndex = args.indexOf('-o');
  if (requestIndex < 0 || outputIndex < 0) throw new Error('Expected --request and -o');
  const request = JSON.parse(await readFile(args[requestIndex + 1], 'utf8'));
  const sourcePaths = args.slice(requestIndex + 2, outputIndex);
  if (sourcePaths.length !== request.Sources.length) throw new Error('Source count mismatch');
  const sources = await Promise.all(sourcePaths.map(async (fileName) => ({ fileName, source: await readFile(fileName, 'utf8') })));
  for (let index = 0; index < sources.length; index++) {
    const parsed = parse(sources[index].source);
    if (parsed.errors.length) {
      for (const error of parsed.errors) console.error(`${sources[index].fileName}:${error.line}:${error.column}: error: ${error.message.replaceAll('\n', ' ')}`);
      process.exit(1);
    }
    if (parsed.ast.configurations.length) throw new Error('Existing CONFIGURATION is unsupported; tasks come from the immutable project snapshot');
    const mapped = request.ProgramNames[request.Sources[index].ItemId];
    if (mapped && !parsed.ast.programs.some(program => program.name.toUpperCase() === mapped.toUpperCase()))
      throw new Error(`Program mapping '${mapped}' does not match source ${basename(sources[index].fileName)}`);
  }
  const task = request.Tasks.filter(task => task.Enabled);
  if (task.length !== 1) throw new Error('Exactly one enabled task is supported');
  const programName = request.ProgramNames[task[0].ProgramId];
  if (!programName || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(programName)) throw new Error('Task program mapping missing or invalid');
  if (!Number.isSafeInteger(task[0].IntervalMilliseconds) || task[0].IntervalMilliseconds <= 0) throw new Error('Invalid task interval');
  const taskName = 'TASK_' + task[0].Name.replace(/[^A-Za-z0-9_]/g, '_').toUpperCase();
  const config = `CONFIGURATION Config0\nRESOURCE Res0 ON PLC\nTASK ${taskName}(INTERVAL := T#${task[0].IntervalMilliseconds}ms, PRIORITY := 0);\nPROGRAM Instance0 WITH ${taskName} : ${programName};\nEND_RESOURCE\nEND_CONFIGURATION\n`;
  const result = compile(sources[0].source, {
    fileName: sources[0].fileName, headerFileName: 'generated.hpp', md5: request.ProgramMd5,
    additionalSources: [...sources.slice(1), { fileName: '__workbench_configuration.st', source: config }],
    libraries: discoverStlibs(join(request.CompilerDistributionDirectory, 'libs')),
  });
  for (const diagnostic of [...result.errors, ...result.warnings])
    console.error(`${diagnostic.file ?? sources[0].fileName}:${diagnostic.line}:${diagnostic.column}: ${diagnostic.severity}: ${diagnostic.message.replaceAll('\n', ' ')}`);
  if (!result.success) process.exit(1);
  if (!result.debugTableCpp || !result.debugMap) throw new Error('Compiler omitted authoritative debug artifacts');
  const directory = dirname(args[outputIndex + 1]);
  await writeFile(args[outputIndex + 1], result.cppCode);
  await writeFile(join(directory, 'generated.hpp'), result.headerCode);
  await writeFile(join(directory, 'generated_debug.cpp'), result.debugTableCpp);
  await writeFile(join(directory, 'debug-map.json'), JSON.stringify(result.debugMap, null, 2));
  await writeFile(join(directory, 'configuration.st'), config);
  console.log('Generated C++ and authoritative debugger tables for isolated Runtime candidate compilation.');
} catch (error) {
  console.error(`compiler-adapter:0:0: error: ${error.message}`);
  process.exit(1);
}
