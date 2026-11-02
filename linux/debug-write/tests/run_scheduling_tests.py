"""Run native scheduling tests and the real startup transaction with OS-boundary fakes.

The startup code is extracted unchanged from plc_state_manager.cpp, so these tests
exercise its partial-create/configuration cleanup and RUNNING publication order.
They require no controller, real-time privileges, or Linux machine.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile
import os

p = argparse.ArgumentParser()
p.add_argument('--cxx', default=os.environ.get('CXX', 'c++'))
a = p.parse_args()
here = Path(__file__).resolve().parent
source = (here.parent / 'src/plc_state_manager.cpp').read_text(encoding='utf-8')
start = source.index('    size_t spawned = 0;')
end = source.index('    /* Completion-signal condvar', start)
transaction = source[start:end]
prelude = r'''
#include "workbench_task_scheduling.h"
#include <cassert>
#include <cerrno>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <vector>
#include <cstdlib>
#undef assert
#define assert(x) do {if (!(x)) {std::fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x);std::exit(1);}} while(0)
#define __linux__ 1
#define SCHED_OTHER 0
#define SCHED_FIFO 1
#define CPU_ZERO(x) ((x)->cpu=-1)
#define CPU_SET(c,x) ((x)->cpu=c)
#define SIGUSR1 10
#define SIGFPE 8
#define SIGSEGV 11
#define SIG_DFL nullptr
#define PLC_FIFO_DISPATCHER 98
struct cpu_set_t { int cpu; };
struct sched_param { int sched_priority; };
struct Task { int thread; char name[8]; int priority; int go; int tracker; };
using PlcTaskCtx = Task;
Task *plc_tasks=nullptr;
size_t plc_task_count=2;
int state_mutex=0, plc_tasks_lock=0;
enum {PLC_STATE_ERROR=3,PLC_STATE_RUNNING=1};
int plc_state=0, creates=0, joins=0, frees=0, publishes=0, cpu_calls=0;
int create_error=-1, schedule_error=-1, affinity_error=-1, scheduling_calls=0;
int recorded_policy[8],recorded_priority[8];
bool use_metadata=true;
auto scheduling_getter=true;
std::vector<WorkbenchTaskScheduling> scheduling;
void log_info(const char*,...) {} void log_warn(const char*,...) {} void log_error(const char*,...) {}
void* plc_task_thread(void*) {return nullptr;}
int fake_create(int *t,void*,void*(*)(void*),void*) {
  const int index=creates++; if(index==create_error)return EAGAIN;*t=index+1;return 0;
}
int fake_setsched(int,int p,sched_param *sp) {
  int n=scheduling_calls++; recorded_policy[n]=p;recorded_priority[n]=sp->sched_priority;
  return n==schedule_error?EPERM:0;
}
int fake_affinity(int,size_t,cpu_set_t*) {++cpu_calls;return affinity_error>=0?EINVAL:0;}
int fake_post(int*) {assert(plc_state==PLC_STATE_ERROR);return 0;}
int fake_join(int,void*) {++joins;return 0;}
int fake_kill(int,int) {return 0;}
int fake_lock(int*) {return 0;} int fake_unlock(int*) {return 0;}
int fake_setname(int,const char*) {return 0;} int fake_self() {return 0;}
void scan_cycle_tracker_cleanup(int*) {} void sem_destroy(int*) {}
void fake_free(void*) {++frees;}
void* signal(int,void*) {return nullptr;}
void plc_publish_final_state(int s) {plc_state=s;}
int plc_get_state() {return plc_state;}
bool plc_publish_running_if_claimed() {++publishes;plc_state=PLC_STATE_RUNNING;return true;}
void reap_task_threads() {joins+=(int)plc_task_count;}
int plc_task_fifo_priority(int p,bool*) {return 49-p;}
#define pthread_create fake_create
#define pthread_setschedparam fake_setsched
#define pthread_setaffinity_np fake_affinity
#define pthread_mutex_lock fake_lock
#define pthread_mutex_unlock fake_unlock
#define pthread_kill fake_kill
#define pthread_join fake_join
#define pthread_setname_np fake_setname
#define pthread_self fake_self
#define sem_post fake_post
#define free fake_free
void *startup() {
'''
# std::free in the real code maps to std::fake_free; make that wrapper available.
prelude = prelude.replace('#define free fake_free', 'namespace std { inline void fake_free(void *p) {::fake_free(p);} }\n#define free fake_free')
tests = r'''
 return nullptr;
}
void reset() {
 static Task tasks[2]={{0,"MAIN",10,0,0},{0,"FAST",0,0,0}};
 plc_tasks=tasks;plc_task_count=2;scheduling_getter=use_metadata;
 scheduling={{0,10,-1},{1,0,-1}};
 plc_state=0;creates=joins=frees=publishes=cpu_calls=scheduling_calls=0;
 create_error=schedule_error=affinity_error=-1;
}
void rejected(int expected_joins) {
 assert(plc_state==PLC_STATE_ERROR && publishes==0 && joins==expected_joins);
 assert(plc_tasks==nullptr && plc_task_count==0 && frees==1);
}
int main() {
 reset();startup();assert(plc_state==PLC_STATE_RUNNING&&publishes==1&&joins==0);
 assert(recorded_policy[0]==SCHED_OTHER&&recorded_priority[0]==0);
 assert(recorded_policy[1]==SCHED_FIFO&&recorded_priority[1]==49);
 assert(recorded_policy[2]==SCHED_OTHER&&recorded_priority[2]==0);
 reset();create_error=1;startup();rejected(1);
 reset();schedule_error=0;startup();rejected(1);
 reset();schedule_error=1;startup();rejected(2);
 reset();scheduling[1].cpu=2;affinity_error=0;startup();rejected(2);assert(cpu_calls==1);
 reset();scheduling[1].cpu=2;startup();assert(cpu_calls==1&&publishes==1);
 reset();schedule_error=2;startup();assert(plc_state==PLC_STATE_ERROR&&publishes==0&&joins==2);
 use_metadata=false;reset();schedule_error=0;startup();assert(publishes==1);
 assert(recorded_policy[2]==SCHED_FIFO&&recorded_priority[2]==98);
 puts("runtime startup: ordinary/RT, affinity, partial creation, fail-closed and legacy tests passed");
}
'''
with tempfile.TemporaryDirectory(prefix='plc-task-tests-') as td:
    d=Path(td)
    for test_source in [here / 'task_scheduling_test.cpp', d / 'startup_test.cpp']:
        if test_source.name=='startup_test.cpp':
            test_source.write_text(prelude + transaction + tests, encoding='utf-8')
        exe=d/(test_source.stem+('.exe' if os.name=='nt' else ''))
        subprocess.run([a.cxx,*(['-static'] if os.name=='nt' else []),'-std=c++17','-Wall','-Wextra','-Werror','-I',str(here.parent/'src'),str(test_source),'-o',str(exe)],check=True)
        subprocess.run([str(exe)],check=True,timeout=20)
