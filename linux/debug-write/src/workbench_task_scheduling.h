// SPDX-License-Identifier: MIT
#ifndef WORKBENCH_TASK_SCHEDULING_H
#define WORKBENCH_TASK_SCHEDULING_H

/* Optional program ABI, resolved only on the loaded program's own handle.
 * Missing symbol means legacy upstream semantics. When present every task
 * must return 1 and provide a valid record; no implicit fallback is allowed. */
using WorkbenchTaskSchedulingGetter = int (*)(unsigned, unsigned, int *, int *, int *);
struct WorkbenchTaskScheduling
{
    int policy;    // 0 = SCHED_OTHER, 1 = SCHED_FIFO
    int priority;  // IEC 0 (highest) .. 31
    int cpu;       // -1 = no requested affinity
};

inline bool workbench_task_scheduling_valid(const WorkbenchTaskScheduling &c)
{
    return (c.policy == 0 || c.policy == 1) && c.priority >= 0 &&
           c.priority <= 31 && c.cpu >= -1;
}

inline bool workbench_read_task_scheduling(WorkbenchTaskSchedulingGetter getter,
    unsigned resource, unsigned task, WorkbenchTaskScheduling &c)
{
    c = {-1, -1, -2};
    return getter && getter(resource, task, &c.policy, &c.priority, &c.cpu) == 1 &&
           workbench_task_scheduling_valid(c);
}

/* Callbacks keep error propagation testable without requiring RT privileges.
 * FIFO 49..18 leaves all PLC task priorities below motion's FIFO 80,
 * the runtime dispatcher/watchdog and default PREEMPT_RT IRQ threads. */
template<class Scheduling, class Affinity>
int workbench_apply_task_scheduling(const WorkbenchTaskScheduling &c,
                                    Scheduling scheduling, Affinity affinity)
{
    const int rc = scheduling(c.policy, c.policy == 1 ? 49 - c.priority : 0);
    if (rc != 0) return rc;
    return c.cpu < 0 ? 0 : affinity(c.cpu);
}
#endif
