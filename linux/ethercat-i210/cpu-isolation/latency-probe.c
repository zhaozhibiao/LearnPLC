// SPDX-License-Identifier: MIT
#define _GNU_SOURCE
#include <sched.h>
#include <sys/mman.h>
#include <time.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
static int64_t ns(struct timespec t){return (int64_t)t.tv_sec*1000000000+t.tv_nsec;}
int main(void){cpu_set_t set;CPU_ZERO(&set);CPU_SET(2,&set);if(sched_setaffinity(0,sizeof set,&set))return perror("affinity"),1;
struct sched_param p={.sched_priority=80};if(sched_setscheduler(0,SCHED_FIFO,&p))return perror("fifo"),1;
if(mlockall(MCL_CURRENT|MCL_FUTURE))return perror("mlockall"),1;
struct timespec now;clock_gettime(CLOCK_MONOTONIC,&now);int64_t next=ns(now)+1000000,max=0,sum=0;unsigned over=0,wrong=0;
for(int i=0;i<30000;++i){struct timespec at={next/1000000000,next%1000000000};int e=clock_nanosleep(CLOCK_MONOTONIC,TIMER_ABSTIME,&at,0);if(e)return 2;clock_gettime(CLOCK_MONOTONIC,&now);int64_t late=ns(now)-next;if(late>max)max=late;sum+=late;if(late>=1000000)++over;if(sched_getcpu()!=2)++wrong;next+=1000000;if(next<=ns(now))next+=((ns(now)-next)/1000000+1)*1000000;}
p.sched_priority=0;sched_setscheduler(0,SCHED_OTHER,&p);printf("{\"samples\":30000,\"periodNs\":1000000,\"cpu\":2,\"fifo\":80,\"maximumWakeLatenessNs\":%ld,\"meanWakeLatenessNs\":%ld,\"overPeriod\":%u,\"wrongCpu\":%u,\"motionCommands\":0}\n",(long)max,(long)(sum/30000),over,wrong);return wrong?1:0;}
