#include "motion_setpoint.hpp"
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <stdexcept>
using namespace ecmotion;
void checked(bool b,int line){if(!b){std::fprintf(stderr,"dynamic profile invariant at %d\n",line);std::exit(1);}}
#define check(b) checked(b,__LINE__)
int main(){
 MotionSetpointGenerator g({.001,-25,25,2,2,3,20});g.synchronize(0);
 for(double bad:{-1.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN(),2.01}) check(!g.move_absolute(1,1,bad,1,10));
 check(!g.move_absolute(1,1,1,3.01,10));check(!g.move_absolute(1,1,1,1,20.01));
 check(g.move_absolute(1,1,.5,.25,2));
 auto clone=g;check(!g.move_absolute(2,1,-1,1,1));
 double v=0,a=0;bool done=false;
 for(int i=0;i<20000;++i){auto s=g.update(),t=clone.update();check(s.position==t.position);double na=(s.velocity-v)/.001;check(std::abs(na)<=.251);check(std::abs((na-a)/.001)<=2.02);v=s.velocity;a=na;if(s.mode==MotionSetpointMode::Hold){done=true;break;}}check(done);
 MotionSetpointGenerator legacy({.001,-25,25,2,2,3,0});legacy.synchronize(0);check(!legacy.move_absolute(1,1,1,1,1));check(legacy.move_absolute(1,1,0,0,0));
 MotionSetpointGenerator nojerk({.001,-25,25,2,2,3,0});nojerk.synchronize(0);check(nojerk.move_absolute(.001,.1,.5,.25,0,true));
 v=0;done=false;
 for(int i=0;i<20000;++i){auto s=nojerk.update();const double ac=(s.velocity-v)/.001;check(ac<=.501 && ac>=-.251);v=s.velocity;if(s.mode==MotionSetpointMode::Hold){done=true;check(s.position==.001);break;}}check(done);
 MotionSetpointGenerator vgen({.001,-25,25,2,2,3,20});vgen.synchronize(0);
 check(vgen.jog_profile(1,1,1,5));
 double pv=0,pa=0;
 for(int i=0;i<1000;++i){auto s=vgen.update();double a=(s.velocity-pv)/.001;check(std::abs(a)<=1.001);check(std::abs((a-pa)/.001)<=5.01);pv=s.velocity;pa=a;}
 check(vgen.jog_profile(-1,1,1,5));
 for(int i=0;i<500;++i){auto s=vgen.update();double a=(s.velocity-pv)/.001;check(std::abs(a)<=1.001);check(std::abs((a-pa)/.001)<=5.01);pv=s.velocity;pa=a;}
 auto old=vgen;check(!vgen.stop_profile(-1,5));check(vgen.current().position==old.current().position);
 check(vgen.stop_profile(1,5));bool stopped=false;
 for(int i=0;i<10000;++i){auto s=vgen.update();double a=(s.velocity-pv)/.001;check(std::abs(a)<=1.001);check(std::abs((a-pa)/.001)<=5.01);pv=s.velocity;pa=a;if(s.mode==MotionSetpointMode::Hold){stopped=true;break;}}check(stopped);
 MotionSetpointGenerator edge({.001,-1,1,2,2,3,20});edge.synchronize(.99);check(!edge.jog_profile(2,1,1,5));
 MotionSetpointGenerator limit({.001,-2,2,2,2,3,20});limit.synchronize(0);check(limit.jog_profile(1,1,1,5));
 pv=0;pa=0;stopped=false;
 for(int i=0;i<20000;++i){auto st=limit.update();check(st.position>=-2 && st.position<=2);double ac=(st.velocity-pv)/.001;check(std::abs(ac)<=1.001);check(std::abs((ac-pa)/.001)<=5.01);pv=st.velocity;pa=ac;
  auto interrupt=limit;if(!interrupt.stop_profile(1,5)){std::fprintf(stderr,"i=%d x=%.17g v=%.17g a=%.17g mode=%d\n",i,st.position,st.velocity,ac,int(st.mode));check(false);}
  if(st.mode==MotionSetpointMode::Hold){stopped=true;check(st.position<2);break;}}
 check(stopped);
 MotionSetpointGenerator reverse({.001,-25,25,2,2,3,20});reverse.synchronize(0);check(reverse.jog_profile(1,.5,1.5,2));
 for(int i=0;i<5000;++i) static_cast<void>(reverse.update());
 check(reverse.current().velocity==1);check(reverse.jog_profile(-1,.5,1.5,2));
 pv=1;pa=0;
 for(int i=0;i<10000;++i){auto st=reverse.update();double ac=(st.velocity-pv)/.001;check(std::abs(ac)<=.501);check(std::abs((ac-pa)/.001)<=2.01);pv=st.velocity;pa=ac;}
 check(reverse.current().velocity==-1);const double before_stop=reverse.current().position;
 check(reverse.stop_profile(1,5));stopped=false;
 for(int i=0;i<10000;++i){auto st=reverse.update();if(st.mode==MotionSetpointMode::Hold){stopped=true;break;}}
 check(stopped);const double distance=before_stop-reverse.current().position;check(distance>=.75 && distance<1);
 for(double bad:{-1.0,std::numeric_limits<double>::infinity(),std::numeric_limits<double>::quiet_NaN()}) {
   check(!reverse.jog_profile(1,bad,1,1));check(!reverse.jog_profile(1,1,bad,1));check(!reverse.jog_profile(1,1,1,bad));
   check(!reverse.stop_profile(bad,1));check(!reverse.stop_profile(1,bad));
 }
 check(!reverse.jog_profile(1,2.01,1,1));check(!reverse.jog_profile(1,1,3.01,1));check(!reverse.jog_profile(1,1,1,20.01));
 check(!reverse.stop_profile(3.01,1));check(!reverse.stop_profile(1,20.01));
}



