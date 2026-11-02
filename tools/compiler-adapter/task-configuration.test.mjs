import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createTaskConfiguration } from './task-configuration.mjs';
import { compile } from 'strucpp/node';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const task = (extra = {}) => ({ Enabled: true, Name: 'Main', ProgramId: 'p', IntervalMilliseconds: 20, ...extra });
const request = tasks => ({ Tasks: tasks, ProgramNames: { p: 'Demo' } });
test('multiple independent instances preserve IEC priority and explicit policy', () => {
  const result = createTaskConfiguration(request([task(), task({ Name: 'Fast', Scheduling: 1, Priority: 2, Cpu: 3 })]));
  assert.match(result.config, /TASK TASK_MAIN\(INTERVAL := T#20ms, PRIORITY := 10\)/);
  assert.match(result.config, /PROGRAM Instance1 WITH TASK_FAST : Demo/);
  assert.deepEqual(result.tasks.map(t => [t.Scheduling, t.Priority, t.Cpu]), [[0,10,null],[1,2,3]]);
  const compiled = compile('PROGRAM Demo\nVAR x : INT; END_VAR\nx := x + 1;\nEND_PROGRAM', { additionalSources: [{fileName: 'config.st', source: result.config}] });
  assert.equal(compiled.success, true, JSON.stringify(compiled.errors));
  assert.match(compiled.cppCode, /TASK_MAIN/);
  assert.match(result.metadataCpp, /workbench_task_scheduling_v1/);
});
test('Main honors explicitly requested realtime', () => assert.equal(createTaskConfiguration(request([task({ Scheduling: 1 })])).tasks[0].Scheduling, 1));
test('reject invalid scheduling input and collisions', () => {
  for (const invalid of [{Scheduling: 2}, {Scheduling: null}, {Priority: -1}, {Priority: 32}, {Priority: 0.5}, {Cpu: -1}, {Cpu: 1.5}, {Cpu: Number.MAX_SAFE_INTEGER}, {IntervalMilliseconds: 0}])
    assert.throws(() => createTaskConfiguration(request([task(invalid)])));
  assert.throws(() => createTaskConfiguration(request([task(),task({Name:'main'})])));
  assert.throws(() => createTaskConfiguration(request([])));
});
test('base CLI emits all native task tables and metadata; bridge CLIs reject multiple scans', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'workbench-task-test-'));
  try {
    const root = dirname(fileURLToPath(import.meta.url));
    const source = join(directory, 'demo.st');
    const snapshot = join(directory, 'request.json');
    const output = join(directory, 'generated.cpp');
    await writeFile(source, 'PROGRAM Demo\nVAR x : INT; END_VAR\nx := x + 1;\nEND_PROGRAM');
    await writeFile(snapshot, JSON.stringify({ ...request([task(), task({ Name: 'Fast', Scheduling: 1, Priority: 0, Cpu: 2 })]), Sources: [{ ItemId: 'p' }], CompilerDistributionDirectory: join(root, 'node_modules/strucpp'), ProgramMd5: '12345678901234567890123456789012' }));
    const run = adapter => spawnSync(process.execPath, [join(root, adapter), '--request', snapshot, source, '-o', output], {encoding: 'utf8'});
    const base = run('compile.mjs');
    assert.equal(base.status, 0, base.stderr);
    const cpp = await readFile(output, 'utf8');
    assert.match(cpp, /tasks_storage\[0\] = TaskInstance\("TASK_MAIN"/);
    assert.match(cpp, /tasks_storage\[1\] = TaskInstance\("TASK_FAST"/);
    assert.match(cpp, /case 1: \*policy = 1; \*priority = 0; \*cpu = 2; return 1/);
    for (const adapter of ['compile-axis.mjs', 'compile-motion.mjs', 'compile-extended.mjs']) {
      const bridge = run(adapter);
      assert.equal(bridge.status, 1);
      assert.match(bridge.stderr, /Scan bridge adapters require exactly one enabled task/);
    }
  } finally { await rm(directory, {recursive: true, force: true}); }
});
