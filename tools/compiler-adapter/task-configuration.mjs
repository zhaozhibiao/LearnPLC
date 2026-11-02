// SPDX-License-Identifier: GPL-3.0-or-later
// Immutable snapshot task declarations and versioned native scheduling metadata.
export function createTaskConfiguration(request, { singleTask = false } = {}) {
  const enabled = request.Tasks.filter(task => task.Enabled);
  if (!enabled.length) throw new Error('At least one enabled task is required');
  if (singleTask && enabled.length !== 1) throw new Error('Scan bridge adapters require exactly one enabled task');
  const names = new Set();
  const tasks = enabled.map(task => {
    if (typeof task.Name !== 'string' || !task.Name.trim()) throw new Error('Invalid task name');
    const taskName = 'TASK_' + task.Name.replace(/[^A-Za-z0-9_]/g, '_').toUpperCase();
    if (names.has(taskName)) throw new Error('Task names collide after IEC normalization');
    names.add(taskName);
    const programName = request.ProgramNames[task.ProgramId];
    if (typeof programName !== 'string' || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(programName)) throw new Error('Task program mapping missing or invalid');
    if (!Number.isSafeInteger(task.IntervalMilliseconds) || task.IntervalMilliseconds <= 0) throw new Error('Invalid task interval');
    const Scheduling = task.Scheduling === undefined ? 0 : task.Scheduling;
    const Priority = task.Priority === undefined ? 10 : task.Priority;
    const Cpu = task.Cpu === undefined ? null : task.Cpu;
    if (Scheduling !== 0 && Scheduling !== 1) throw new Error('Invalid task scheduling policy');
    if (!Number.isSafeInteger(Priority) || Priority < 0 || Priority > 31) throw new Error('IEC task priority must be 0..31');
    if (Cpu !== null && (!Number.isSafeInteger(Cpu) || Cpu < 0 || Cpu > 2147483647)) throw new Error('Task CPU must be a nonnegative 32-bit integer or null');
    return { ...task, Scheduling, Priority, Cpu, taskName, programName };
  });
  const declarations = tasks.map(task => `TASK ${task.taskName}(INTERVAL := T#${task.IntervalMilliseconds}ms, PRIORITY := ${task.Priority});`);
  const instances = tasks.map((task, i) => `PROGRAM Instance${i} WITH ${task.taskName} : ${task.programName};`);
  const config = `CONFIGURATION Config0\nRESOURCE Res0 ON PLC\n${[...declarations, ...instances].join('\n')}\nEND_RESOURCE\nEND_CONFIGURATION\n`;
  // The compiler emits resource/task tables in declaration order. Index lookup
  // avoids ambiguity from the runtime's fixed-size task name field.
  const metadataCpp = `\nextern "C" int workbench_task_scheduling_v1(unsigned resource_index, unsigned task_index, int *policy, int *priority, int *cpu) {\n` +
    `    if (!policy || !priority || !cpu) return -1;\n    if (resource_index != 0) return 0;\n    switch (task_index) {\n` +
    tasks.map((task, i) => `    case ${i}: *policy = ${task.Scheduling}; *priority = ${task.Priority}; *cpu = ${task.Cpu ?? -1}; return 1;`).join('\n') +
    '\n    default: return 0;\n    }\n}\n';
  return { tasks, config, metadataCpp };
}
