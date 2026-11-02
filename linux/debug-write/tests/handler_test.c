/* SPDX-License-Identifier: MIT */
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <pthread.h>
#include "image_tables.h"
#include "debug_write_journal.h"
#include "debug_handler.h"

static unsigned enqueued, direct_calls;
static int queue_rc;
static uint16_t leaf_size = 1;
static uint8_t got_arr, got_op, got_byte;
static uint16_t got_elem, got_len;
static bool block_enqueue, entered_enqueue, allow_enqueue, invalidated;
static pthread_mutex_t test_lock=PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t test_cv=PTHREAD_COND_INITIALIZER;
static uint8_t arrays(void) { return 2; }
static uint16_t elems(uint8_t a) { (void)a; return 300; }
static uint16_t size_of(uint8_t a,uint16_t e) { (void)a;(void)e;return leaf_size; }
static uint8_t set(uint8_t a,uint16_t e,bool f,const uint8_t *b,uint16_t n)
{ (void)a;(void)e;(void)f;(void)b;(void)n; ++direct_calls; return 1; }
static uint8_t write_soft(uint8_t a,uint16_t e,const uint8_t *b,uint16_t n)
{ (void)a;(void)e;(void)b;(void)n; ++direct_calls; return 1; }
static uint16_t read_leaf(uint8_t a,uint16_t e,uint8_t *b)
{ (void)a;(void)e;*b=1;return 1; }
uint8_t (*ext_strucpp_debug_array_count)(void)=arrays;
uint16_t (*ext_strucpp_debug_elem_count)(uint8_t)=elems;
uint16_t (*ext_strucpp_debug_size)(uint8_t,uint16_t)=size_of;
uint8_t (*ext_strucpp_debug_set)(uint8_t,uint16_t,bool,const uint8_t*,uint16_t)=set;
uint16_t (*ext_strucpp_debug_read)(uint8_t,uint16_t,uint8_t*)=read_leaf;
uint8_t (*ext_strucpp_debug_write)(uint8_t,uint16_t,const uint8_t*,uint16_t)=write_soft;
const char *ext_strucpp_program_md5="0123456789abcdef0123456789abcdef";
unsigned long scan_counter=9;
int test_plc_state=1;
int plc_get_state(void) { return test_plc_state; }
void debug_write_journal_reset(void) { enqueued=0; }
int runtime_external_write(uint8_t a,uint16_t e,uint8_t op,const uint8_t *b,uint16_t n)
{ ++enqueued;got_arr=a;got_elem=e;got_op=op;got_len=n;got_byte=(b&&n)?*b:0;
  pthread_mutex_lock(&test_lock);if(block_enqueue){entered_enqueue=true;pthread_cond_broadcast(&test_cv);while(!allow_enqueue)pthread_cond_wait(&test_cv,&test_lock);}pthread_mutex_unlock(&test_lock);return queue_rc; }
static unsigned checks;
#define CHECK(x) do { ++checks; if(!(x)) { fprintf(stderr,"FAIL line %d: %s\n",__LINE__,#x); exit(1); } } while(0)
static void request(uint8_t *p) { memset(p,0,4096);p[0]=0x46;p[1]=1;memcpy(p+2,"0123456789abcdef0123456789abcdef",32);p[34]=1;p[35]=1;p[36]=2;p[37]=1;enqueued=0; }
static void status(uint8_t *p,size_t n,uint8_t s) { CHECK(process_debug_data(p,n)==2);CHECK(p[0]==0x46);CHECK(p[1]==s); }
static void *writer(void *arg) { status(arg,38,0x7e);return NULL; }
static void *invalidator(void *arg) { (void)arg;debug_write_program_invalidate();pthread_mutex_lock(&test_lock);invalidated=true;pthread_mutex_unlock(&test_lock);return NULL; }
int main(void)
{
    uint8_t p[4096]={0x46,0};
    CHECK(process_debug_data(p,2)==3);CHECK(p[0]==0x46&&p[1]==0x7e&&p[2]==1);CHECK(!enqueued);
    p[0]=0x46;p[1]=2;CHECK(process_debug_data(p,2)==3);CHECK(p[0]==0x46&&p[1]==0x7e&&p[2]==1);CHECK(!enqueued);
    request(p);status(p,38,0x83);CHECK(!enqueued);
    debug_write_program_begin_load();debug_write_program_end_load(true);
    /* Pause an enqueue while unload starts: invalidate/reset must wait until
     * identity + address + enqueue has left the lifecycle gate. */
    request(p);block_enqueue=true;pthread_t w,u;CHECK(pthread_create(&w,NULL,writer,p)==0);
    pthread_mutex_lock(&test_lock);while(!entered_enqueue)pthread_cond_wait(&test_cv,&test_lock);pthread_mutex_unlock(&test_lock);
    CHECK(pthread_create(&u,NULL,invalidator,NULL)==0);
    pthread_mutex_lock(&test_lock);CHECK(!invalidated);allow_enqueue=true;pthread_cond_broadcast(&test_cv);pthread_mutex_unlock(&test_lock);
    CHECK(pthread_join(w,NULL)==0);CHECK(pthread_join(u,NULL)==0);CHECK(invalidated&&!enqueued);block_enqueue=false;
    debug_write_program_begin_load();debug_write_program_end_load(true);
    request(p);status(p,38,0x7e);CHECK(enqueued==1&&got_arr==1&&got_elem==258&&got_op==DBGW_OP_WRITE&&got_len==1&&got_byte==1);CHECK(!direct_calls);
    request(p);p[37]=0;status(p,38,0x7e);CHECK(got_byte==0);
    for(size_t n=0;n<40;++n) { if(n==38)continue;request(p);if(!n){CHECK(process_debug_data(p,n)==0);}else{status(p,n,0x81);}CHECK(!enqueued); }
    request(p);p[1]=2;status(p,38,0x81);CHECK(!enqueued);
    request(p);p[37]=2;status(p,38,0x81);CHECK(!enqueued);
    request(p);p[34]=2;status(p,38,0x81);CHECK(!enqueued);
    request(p);p[35]=1;p[36]=44;status(p,38,0x81);CHECK(!enqueued);
    request(p);leaf_size=2;status(p,38,0x81);CHECK(!enqueued);leaf_size=1;
    request(p);leaf_size=0;status(p,38,0x81);CHECK(!enqueued);leaf_size=1;
    request(p);p[2]='x';status(p,38,0x84);CHECK(!enqueued);
    request(p);ext_strucpp_program_md5="short";memcpy(p+2,"short",6);status(p,38,0x83);CHECK(!enqueued);ext_strucpp_program_md5="0123456789abcdef0123456789abcdef";
    request(p);ext_strucpp_program_md5=NULL;status(p,38,0x83);CHECK(!enqueued);ext_strucpp_program_md5="0123456789abcdef0123456789abcdef";
    request(p);ext_strucpp_debug_write=NULL;status(p,38,0x83);CHECK(!enqueued);ext_strucpp_debug_write=write_soft;
    request(p);ext_strucpp_debug_read=NULL;status(p,38,0x83);CHECK(!enqueued);ext_strucpp_debug_read=read_leaf;
    request(p);queue_rc=-1;status(p,38,0x82);CHECK(enqueued==1);queue_rc=0;
    request(p);test_plc_state=0;status(p,38,0x83);CHECK(!enqueued);test_plc_state=1;
    debug_write_program_invalidate();request(p);status(p,38,0x83);CHECK(!enqueued);
    debug_write_program_begin_load();debug_write_program_end_load(false);request(p);status(p,38,0x83);CHECK(!enqueued);
    debug_write_program_begin_load();debug_write_program_end_load(true);
    /* Existing force/unforce and read/info/identity dispatch must survive. */
    memset(p,0,sizeof p);p[0]=0x42;p[4]=1;p[6]=1;p[7]=1;CHECK(process_debug_data(p,8)==2);CHECK(p[1]==0x7e&&got_op==DBGW_OP_FORCE);
    memset(p,0,sizeof p);p[0]=0x42;CHECK(process_debug_data(p,7)==2);CHECK(got_op==DBGW_OP_UNFORCE);
    p[0]=0x41;CHECK(process_debug_data(p,1)==7);CHECK(p[2]==0x7e);
    memset(p,0,sizeof p);p[0]=0x43;CHECK(process_debug_data(p,6)==11);CHECK(p[1]==0x7e&&p[10]==1);
    memset(p,0,sizeof p);p[0]=0x44;p[2]=1;CHECK(process_debug_data(p,6)==11);CHECK(p[1]==0x7e&&p[10]==1);
    p[0]=0x45;CHECK(process_debug_data(p,3)==36);CHECK(!memcmp(p+2,ext_strucpp_program_md5,32));CHECK(!direct_calls);
    printf("PASS: %u handler assertions\n",checks);return 0;
}
