#include "motion_setpoint.hpp"
#include "motion_safety.hpp"
#include <cmath>
#include <cstdio>
#include <stdexcept>
#include <limits>
using namespace ecmotion;
static void require(bool v) { if (!v) throw std::runtime_error("smooth profile invariant failed"); }
int main() {
    MotionSetpointGenerator g({.001,-25,25,2,2,2,20});
    g.synchronize(0);
    require(g.move_absolute(1,1));
    double prev_v=0,prev_a=0,peak_v=0,peak_a=0,peak_j=0;
    bool done=false;
    for(int i=0;i<10000;i++) {
        auto s=g.update();
        double a=(s.velocity-prev_v)/.001, j=(a-prev_a)/.001;
        peak_v=std::max(peak_v,std::abs(s.velocity));
        peak_a=std::max(peak_a,std::abs(a));
        peak_j=std::max(peak_j,std::abs(j));
        require(s.position>=-1e-12 && s.position<=1+1e-12);
        prev_v=s.velocity;prev_a=a;
        if(s.mode==MotionSetpointMode::Hold) {done=true;require(std::abs(s.position-1)<1e-9);break;}
    }
    std::fprintf(stderr,"done=%d v=%f a=%f j=%f\\n",done,peak_v,peak_a,peak_j); require(done && peak_v<=1.001 && peak_a<=2.02 && peak_j<=20.5);
    require(g.move_absolute(0,1));
    done=false;
    for(int i=0;i<10000;i++) { auto s=g.update();require(s.position>=-1e-12 && s.position<=1+1e-12);if(s.mode==MotionSetpointMode::Hold){done=true;require(std::abs(s.position)<1e-9);break;} }
    require(done);
    // Unsupported smooth reentry must reject without replacing the active trajectory.
    require(g.move_absolute(1,1));
    for(int i=0;i<100;++i) static_cast<void>(g.update());
    const auto moving=g.current();
    require(moving.velocity>0);
    require(!g.move_absolute(-1,1));
    require(g.current().position==moving.position && g.current().velocity==moving.velocity);
    g.stop();
    require(!g.move_absolute(-1,1));
    for(int i=0;i<10000 && g.current().mode!=MotionSetpointMode::Hold;++i) {
        const auto s=g.update();require(s.velocity>=0 && s.position>=moving.position);
    }
    require(g.current().mode==MotionSetpointMode::Hold && g.current().velocity==0);
    // Synchronize is the Disable/fault interruption boundary and discards the old curve.
    g.synchronize(-.25);require(g.current().mode==MotionSetpointMode::Hold);
    for(int i=0;i<10;++i) require(g.update().position==-.25);
    const auto nan=std::numeric_limits<double>::quiet_NaN();
    const auto inf=std::numeric_limits<double>::infinity();
    require(!g.move_absolute(nan,1) && !g.move_absolute(0,inf));
    require(!g.move_absolute(inf,1) && !g.move_absolute(0,nan));
    require(!g.move_absolute(26,1) && !g.move_absolute(0,2.001));
    g.synchronize(nan);
    require(!g.move_absolute(0,1)); // Invalid feedback invalidates an old synchronization.
    for(double target : {0.0,1e-12,-1e-12,25.0,-25.0}) {
        g.synchronize(target==25.0?24.999:target==-25.0?-24.999:0.0);
        require(g.move_absolute(target,2));
        double last=g.current().position;
        const bool forwards=target>=last;done=false;
        for(int i=0;i<100000;++i) {
            const auto s=g.update();require(std::isfinite(s.position) && std::isfinite(s.velocity));
            require(s.position>=-25 && s.position<=25);
            require(forwards?s.position>=last-1e-12:s.position<=last+1e-12);last=s.position;
            if(s.mode==MotionSetpointMode::Hold){require(s.position==target && s.velocity==0);done=true;break;}
        }
        require(done);
    }
    // Overflowing curve arithmetic is rejected before it corrupts state.
    MotionSetpointGenerator extreme({.001,-1e308,1e308,2,2,2,20});
    extreme.synchronize(-1e308);require(!extreme.move_absolute(1e308,1));
    const MotionSetpointConfiguration baseline{.001,-25,25,2,2,2,20};
    for(int field=0;field<7;++field) {
        for(double invalid : {nan,inf}) {
            auto c=baseline;
            switch(field) {
                case 0:c.cycle_seconds=invalid;break;
                case 1:c.soft_limit_negative=invalid;break;
                case 2:c.soft_limit_positive=invalid;break;
                case 3:c.maximum_velocity=invalid;break;
                case 4:c.maximum_acceleration=invalid;break;
                case 5:c.maximum_deceleration=invalid;break;
                case 6:c.maximum_jerk=invalid;break;
            }
            bool rejected=false;try{MotionSetpointGenerator bad(c);}catch(const std::invalid_argument&){rejected=true;}
            require(rejected);
        }
    }
    for(double jerk : {-1.0,0.0,1e-9,1e9}) {
        auto c=baseline;c.maximum_jerk=jerk;bool rejected=false;
        try{MotionSetpointGenerator boundary(c);boundary.synchronize(0);require(boundary.move_absolute(1e-12,2));}
        catch(const std::invalid_argument&){rejected=true;}
        require(rejected==(jerk<0));
    }
    bool timeout_rejected=false;
    try{MotionSafetyMonitor bad({1e-300,-2,2,1,.1,1e300});}
    catch(const std::invalid_argument&){timeout_rejected=true;}
    require(timeout_rejected);
}
