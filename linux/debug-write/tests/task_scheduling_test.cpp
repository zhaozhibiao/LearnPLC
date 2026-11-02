#include "../src/workbench_task_scheduling.h"
#include <cassert>
#include <cerrno>
#include <cstdio>
static int record_status=1;
static int record_policy=0;
static int record(unsigned r,unsigned t,int *policy,int *priority,int *cpu) {
    assert(r==0 && t==2);*policy=record_policy;*priority=10;*cpu=-1;return record_status;
}
int main() {
    WorkbenchTaskScheduling c{0, 7, -1};
    assert(workbench_task_scheduling_valid(c));
    int policy=-1, priority=-1, cpu=-2;
    auto scheduling=[&](int p,int r){policy=p;priority=r;return 0;};
    auto affinity=[&](int a){cpu=a;return 0;};
    assert(workbench_apply_task_scheduling(c, scheduling, affinity)==0);
    assert(policy==0 && priority==0 && cpu==-2);
    c={1,0,3};
    assert(workbench_apply_task_scheduling(c,scheduling,affinity)==0);
    assert(policy==1 && priority==49 && priority<80 && cpu==3);
    c.priority=31;
    assert(workbench_apply_task_scheduling(c,scheduling,affinity)==0 && priority==18);
    c.priority=32; assert(!workbench_task_scheduling_valid(c));
    c={2,0,-1}; assert(!workbench_task_scheduling_valid(c));
    c={0,0,-2}; assert(!workbench_task_scheduling_valid(c));
    c={1,0,4}; cpu=-2;
    auto denied=[](int,int){return EPERM;};
    assert(workbench_apply_task_scheduling(c,denied,affinity)==EPERM && cpu==-2);
    auto bad_cpu=[](int){return EINVAL;};
    assert(workbench_apply_task_scheduling(c,scheduling,bad_cpu)==EINVAL);
    c={0,0,-1};
    assert(workbench_apply_task_scheduling(c,denied,affinity)==EPERM);
    assert(workbench_read_task_scheduling(record,0,2,c) && c.policy==0 && c.priority==10);
    record_status=0; assert(!workbench_read_task_scheduling(record,0,2,c));
    record_status=-1; assert(!workbench_read_task_scheduling(record,0,2,c));
    record_status=1;record_policy=2;assert(!workbench_read_task_scheduling(record,0,2,c));
    puts("task scheduling: policy, priorities, affinity and fail-closed tests passed");
}
