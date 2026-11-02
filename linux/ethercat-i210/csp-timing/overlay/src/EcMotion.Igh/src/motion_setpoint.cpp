#include "motion_setpoint.hpp"

#include <algorithm>
#include <cmath>
#include <stdexcept>

namespace ecmotion {
namespace {
bool positive_finite(double value) noexcept {
    return std::isfinite(value) && value > 0.0;
}
}

MotionSetpointGenerator::MotionSetpointGenerator(MotionSetpointConfiguration configuration)
    : configuration_(configuration), active_acceleration_(configuration.maximum_acceleration),
      active_deceleration_(configuration.maximum_deceleration) {
    if (!positive_finite(configuration_.cycle_seconds)
        || !std::isfinite(configuration_.soft_limit_negative)
        || !std::isfinite(configuration_.soft_limit_positive)
        || configuration_.soft_limit_negative >= configuration_.soft_limit_positive
        || !positive_finite(configuration_.maximum_velocity)
        || !positive_finite(configuration_.maximum_acceleration)
        || !positive_finite(configuration_.maximum_deceleration)
        || !std::isfinite(configuration_.maximum_jerk)
        || configuration_.maximum_jerk < 0.0) {
        throw std::invalid_argument("Motion setpoint configuration is invalid.");
    }
}

void MotionSetpointGenerator::synchronize(double actual_position) noexcept {
    if (!std::isfinite(actual_position)) {
        synchronized_ = false;
        current_.velocity = 0.0;
        current_.mode = MotionSetpointMode::Hold;
        smooth_duration_ = 0.0;
        return;
    }
    current_.position = actual_position;
    current_.velocity = 0.0;
    current_.mode = MotionSetpointMode::Hold;
    target_position_ = current_.position;
    commanded_velocity_ = 0.0;
    smooth_duration_ = 0.0;
    velocity_profile_ = false; acceleration_ = 0.0;
    synchronized_ = actual_position >= configuration_.soft_limit_negative
        && actual_position <= configuration_.soft_limit_positive;
}

bool MotionSetpointGenerator::move_absolute(double position, double velocity,
    double acceleration, double deceleration, double jerk, bool profile_v2) noexcept {
    const auto valid = [](double v, double maximum) { return std::isfinite(v) && v >= 0.0 && v <= maximum; };
    if (!valid(acceleration, configuration_.maximum_acceleration)
        || !valid(deceleration, configuration_.maximum_deceleration)
        || !valid(jerk, configuration_.maximum_jerk)) return false;
    const double a = acceleration == 0.0 ? configuration_.maximum_acceleration : acceleration;
    const double d = deceleration == 0.0 ? configuration_.maximum_deceleration : deceleration;
    const double j = jerk == 0.0 ? configuration_.maximum_jerk : jerk;
    if (!synchronized_ || !std::isfinite(position) || !positive_finite(velocity)
        || position < configuration_.soft_limit_negative
        || position > configuration_.soft_limit_positive
        || velocity > configuration_.maximum_velocity
        || ((j > 0.0 || profile_v2)
            && (current_.mode != MotionSetpointMode::Hold || current_.velocity != 0.0))) return false;
    double duration = 0.0;
    if (j > 0.0) {
        const auto length = std::abs(position-current_.position);
        // Compute and validate before changing any active command state.
        const auto velocity_duration=length*1.875/velocity;
        const auto acceleration_duration=std::sqrt(length*5.774/a);
        const auto deceleration_duration=std::sqrt(length*5.774/d);
        const auto jerk_duration=std::cbrt(length*60.0/j);
        duration=std::max({velocity_duration,acceleration_duration,deceleration_duration,
            jerk_duration,2.0*configuration_.cycle_seconds});
        if (!std::isfinite(length) || !positive_finite(duration)) return false;
    }
    const bool trapezoid = profile_v2 && j==0.0;
    double peak=0,ta=0,tc=0;
    if (trapezoid) {
        const double length=std::abs(position-current_.position);
        peak=std::min(velocity,std::sqrt(2*length/(1/a+1/d)));
        ta=peak/a;const double td=peak/d;
        tc=peak>0 ? std::max(0.0,(length-peak*(ta+td)/2)/peak) : 0.0;
        const double total=ta+tc+td;
        if(!std::isfinite(length)||!std::isfinite(total)||!std::isfinite(peak)) return false;
        duration=total;
    }
    active_acceleration_=a;active_deceleration_=d;velocity_profile_=false;
    trapezoid_profile_=trapezoid;trapezoid_peak_=peak;trapezoid_accel_time_=ta;trapezoid_cruise_time_=tc;
    target_position_ = position;
    commanded_velocity_ = velocity;
    smooth_start_ = current_.position;
    smooth_elapsed_ = 0.0;
    smooth_duration_ = duration;
    current_.mode = MotionSetpointMode::MoveAbsolute;
    return true;
}

bool MotionSetpointGenerator::jog(double velocity) noexcept {
    if (!synchronized_ || !std::isfinite(velocity) || velocity == 0.0
        || std::abs(velocity) > configuration_.maximum_velocity) return false;
    velocity_profile_ = false;
    commanded_velocity_ = velocity;
    current_.mode = MotionSetpointMode::Jog;
    return true;
}

void MotionSetpointGenerator::stop() noexcept {
    velocity_profile_ = false; acceleration_ = 0.0;
    smooth_duration_ = 0.0;
    commanded_velocity_ = 0.0;
    current_.mode = std::abs(current_.velocity) > 1e-12
        ? MotionSetpointMode::Stopping : MotionSetpointMode::Hold;
}

double MotionSetpointGenerator::VelocityCurve::position(double t) const noexcept {
    return x + t*(v + t*(a/2.0 + t*(b/3.0 + t*c/4.0)));
}
double MotionSetpointGenerator::VelocityCurve::velocity(double t) const noexcept {
    return v + t*(a + t*(b + t*c));
}

bool MotionSetpointGenerator::curve_inside_limits(const VelocityCurve& q) const noexcept {
    // Partition at velocity extrema. Within each interval velocity is monotone,
    // so every position extremum is either an endpoint or a unique zero of v.
    double cuts[4]{0.0,q.duration,0.0,0.0}; unsigned count=2;
    const auto add=[&](double t) { if (std::isfinite(t) && t>0 && t<q.duration) cuts[count++]=t; };
    const double disc=4*q.b*q.b-12*q.c*q.a;
    if (!std::isfinite(disc)) return false;
    if (q.c!=0 && disc>=0) {add((-2*q.b-std::sqrt(disc))/(6*q.c));add((-2*q.b+std::sqrt(disc))/(6*q.c));}
    else if (q.c==0 && q.b!=0) add(-q.a/(2*q.b));
    if(count>4) return false;
    for(unsigned i=1;i<count;++i) {
        const double value=cuts[i];unsigned j=i;
        while(j>0 && cuts[j-1]>value) {cuts[j]=cuts[j-1];--j;}
        cuts[j]=value;
    }
    const auto valid_position=[&](double t) {const double x=q.position(t);return std::isfinite(x)
        && x>=configuration_.soft_limit_negative && x<=configuration_.soft_limit_positive;};
    for(unsigned i=0;i<count;++i) {
        if (!valid_position(cuts[i]) || !std::isfinite(q.velocity(cuts[i]))
            || std::abs(q.velocity(cuts[i]))>configuration_.maximum_velocity*(1+1e-12)) return false;
        if (i && q.velocity(cuts[i-1])*q.velocity(cuts[i])<0) {
            double lo=cuts[i-1],hi=cuts[i];
            for(unsigned n=0;n<52;++n) {const double mid=(lo+hi)/2;
                if ((q.velocity(lo)>0)==(q.velocity(mid)>0)) lo=mid;else hi=mid;}
            if(!valid_position((lo+hi)/2)) return false;
        }
    }
    return true;
}

bool MotionSetpointGenerator::velocity_curve(double target,double acceleration,double deceleration,
    double jerk,VelocityCurve& q) const noexcept {
    if (!synchronized_ || !std::isfinite(target) || std::abs(target)>configuration_.maximum_velocity
        || !positive_finite(acceleration) || !positive_finite(deceleration)
        || !std::isfinite(jerk) || jerk<0.0) return false;
    // A conservative common magnitude enforces both asymmetric budgets, including reversal.
    const double limit=std::min(acceleration,deceleration);
    if (std::abs(acceleration_)>limit*(1+1e-12)) return false;
    double duration=2*configuration_.cycle_seconds;
    for(unsigned n=0;n<100;++n,duration*=1.25) {
        const double dv=target-current_.velocity;
        q={current_.position,current_.velocity,acceleration_,
            (3*dv-2*acceleration_*duration)/(duration*duration),
            (-2*dv+acceleration_*duration)/(duration*duration*duration),duration};
        double peak_a=std::max(std::abs(q.a),std::abs(q.a+2*q.b*duration+3*q.c*duration*duration));
        if(q.c!=0) {const double t=-q.b/(3*q.c);if(t>0 && t<duration)
            peak_a=std::max(peak_a,std::abs(q.a+2*q.b*t+3*q.c*t*t));}
        const double peak_j=std::max(std::abs(2*q.b),std::abs(2*q.b+6*q.c*duration));
        if(!std::isfinite(peak_a) || !std::isfinite(peak_j)) return false;
        if(peak_a<=limit*(1+1e-12) && (jerk==0 || peak_j<=jerk*(1+1e-12))) return curve_inside_limits(q);
    }
    return false;
}

void MotionSetpointGenerator::begin_velocity_curve(const VelocityCurve& curve,double target,
    double acceleration,double deceleration,double jerk,bool stopping) noexcept {
    velocity_curve_=curve;velocity_elapsed_=0;velocity_profile_=true;smooth_duration_=0;
    commanded_velocity_=target;active_acceleration_=acceleration;active_deceleration_=deceleration;active_jerk_=jerk;
    current_.mode=stopping?MotionSetpointMode::Stopping:MotionSetpointMode::Jog;
}
bool MotionSetpointGenerator::jog_profile(double velocity,double acceleration,double deceleration,double jerk) noexcept {
    const auto valid=[](double v,double m){return std::isfinite(v)&&v>=0&&v<=m;};
    if(!valid(acceleration,configuration_.maximum_acceleration)||!valid(deceleration,configuration_.maximum_deceleration)
        ||!valid(jerk,configuration_.maximum_jerk)||velocity==0) return false;
    const double a=acceleration==0?configuration_.maximum_acceleration:acceleration;
    const double d=deceleration==0?configuration_.maximum_deceleration:deceleration;
    const double j=jerk==0?configuration_.maximum_jerk:jerk;
    VelocityCurve curve, initial_stop;
    if(!velocity_curve(0,d,d,j,initial_stop) || !velocity_curve(velocity,a,d,j,curve)) return false;
    // Admission includes a complete stop after the transition, not just its target.
    auto future=*this;future.current_.position=curve.position(curve.duration);future.current_.velocity=velocity;future.acceleration_=0;
    VelocityCurve stopping;
    if(!future.velocity_curve(0,d,d,j,stopping)) return false;
    begin_velocity_curve(curve,velocity,a,d,j,false);return true;
}
bool MotionSetpointGenerator::stop_profile(double deceleration,double jerk) noexcept {
    if(!std::isfinite(deceleration)||deceleration<0||deceleration>configuration_.maximum_deceleration
        ||!std::isfinite(jerk)||jerk<0||jerk>configuration_.maximum_jerk) return false;
    const double d=deceleration==0?configuration_.maximum_deceleration:deceleration;
    const double j=jerk==0?configuration_.maximum_jerk:jerk;
    // An already accepted stop remains immutable when an equivalent or looser
    // stop request arrives; replanning it can needlessly lengthen stopping distance.
    if(velocity_profile_ && current_.mode==MotionSetpointMode::Stopping && d>=active_deceleration_
        && (j==0 || (active_jerk_>0 && j>=active_jerk_))) return true;
    VelocityCurve curve;if(!velocity_curve(0,d,d,j,curve)) return false;
    begin_velocity_curve(curve,0,d,d,j,true);return true;
}

bool MotionSetpointGenerator::set_external(
    double position, double velocity, bool final_point) noexcept {
    if (!synchronized_ || !std::isfinite(position) || !std::isfinite(velocity)
        || position < configuration_.soft_limit_negative
        || position > configuration_.soft_limit_positive
        || std::abs(velocity) > configuration_.maximum_velocity) return false;
    velocity_profile_ = false;
    acceleration_ = 0.0;
    current_.position = position;
    current_.velocity = final_point ? 0.0 : velocity;
    current_.mode = final_point ? MotionSetpointMode::Hold : MotionSetpointMode::External;
    target_position_ = position;
    commanded_velocity_ = current_.velocity;
    return true;
}

MotionSetpoint MotionSetpointGenerator::update() noexcept {
    if (!synchronized_) return current_;
    const auto dt = configuration_.cycle_seconds;
    if (velocity_profile_) {
        if (velocity_elapsed_<velocity_curve_.duration) {
            const double t=std::min(velocity_elapsed_+dt,velocity_curve_.duration);
            if (current_.mode==MotionSetpointMode::Jog) {
                auto next=*this;next.current_.position=velocity_curve_.position(t);
                next.current_.velocity=velocity_curve_.velocity(t);
                next.acceleration_=velocity_curve_.a+2*velocity_curve_.b*t+3*velocity_curve_.c*t*t;
                VelocityCurve stopping;
                if(!next.velocity_curve(0,active_deceleration_,active_deceleration_,active_jerk_,stopping)) {
                    if(stop_profile(active_deceleration_,active_jerk_)) return update();
                    return current_;
                }
            }
            velocity_elapsed_=t;
            current_.position=velocity_curve_.position(t);current_.velocity=velocity_curve_.velocity(t);
            acceleration_=velocity_curve_.a+2*velocity_curve_.b*t+3*velocity_curve_.c*t*t;
            if(t==velocity_curve_.duration) {current_.velocity=commanded_velocity_;acceleration_=0;
                if(current_.mode==MotionSetpointMode::Stopping){current_.mode=MotionSetpointMode::Hold;velocity_profile_=false;}}
        } else {
            // Reserve one complete next cycle before leaving the currently stoppable state.
            auto next=*this;next.current_.position+=current_.velocity*dt;
            VelocityCurve stopping;
            if(!next.velocity_curve(0,active_deceleration_,active_deceleration_,active_jerk_,stopping)) {
                if(stop_profile(active_deceleration_,active_jerk_)) return update();
                // Admission and the previous cycle's reservation make this unreachable.
                // Retain the last safe setpoint if numerical validation ever disagrees.
                return current_;
            }
            current_.position=next.current_.position;
        }
        return current_;
    }
    const double previous_velocity=current_.velocity;
    if (current_.mode == MotionSetpointMode::MoveAbsolute) {
        if(trapezoid_profile_) {
            smooth_elapsed_=std::min(smooth_elapsed_+dt,smooth_duration_);
            const double t=smooth_elapsed_,ta=trapezoid_accel_time_,tc=trapezoid_cruise_time_,v=trapezoid_peak_;
            const double direction=target_position_>=smooth_start_?1.0:-1.0;
            double distance=0,speed=0;
            if(t<ta) {distance=active_acceleration_*t*t/2;speed=active_acceleration_*t;acceleration_=direction*active_acceleration_;}
            else if(t<ta+tc) {distance=v*ta/2+v*(t-ta);speed=v;acceleration_=0;}
            else {const double elapsed=t-ta-tc;distance=v*ta/2+v*tc+v*elapsed-active_deceleration_*elapsed*elapsed/2;
                speed=std::max(0.0,v-active_deceleration_*elapsed);acceleration_=-direction*active_deceleration_;}
            current_.position=smooth_start_+direction*distance;current_.velocity=direction*speed;
            if(t>=smooth_duration_) {current_.position=target_position_;current_.velocity=0;acceleration_=0;current_.mode=MotionSetpointMode::Hold;trapezoid_profile_=false;}
            return current_;
        }
        if (smooth_duration_ > 0.0) {
            smooth_elapsed_ = std::min(smooth_elapsed_+dt,smooth_duration_);
            const auto u = smooth_elapsed_/smooth_duration_;
            const auto u2=u*u, u3=u2*u, u4=u3*u, u5=u4*u;
            const auto length=target_position_-smooth_start_;
            current_.position=smooth_start_+length*(10*u3-15*u4+6*u5);
            current_.velocity=length*(30*u2-60*u3+30*u4)/smooth_duration_;
            acceleration_=length*(60*u-180*u2+120*u3)/(smooth_duration_*smooth_duration_);
            if (smooth_elapsed_>=smooth_duration_) {
                current_.position=target_position_;current_.velocity=0.0;
                current_.mode=MotionSetpointMode::Hold;smooth_duration_=0.0;acceleration_=0.0;
            }
            return current_;
        }
        const auto distance = target_position_ - current_.position;
        const auto braking_velocity = std::sqrt(
            2.0 * active_deceleration_ * std::abs(distance));
        const auto desired_velocity = std::copysign(
            std::min(commanded_velocity_, braking_velocity), distance);
        const auto rate = current_.velocity * desired_velocity < 0.0
            || std::abs(desired_velocity) < std::abs(current_.velocity)
            ? active_deceleration_ : active_acceleration_;
        current_.velocity = approach(current_.velocity, desired_velocity, rate);
        const auto next = current_.position + current_.velocity * dt;
        if ((distance >= 0.0 && next >= target_position_)
            || (distance < 0.0 && next <= target_position_)) {
            current_.position = target_position_;
            current_.velocity = 0.0;
            current_.mode = MotionSetpointMode::Hold;
        } else {
            current_.position = next;
        }
    } else if (current_.mode == MotionSetpointMode::Jog) {
        const auto distance_to_limit = commanded_velocity_ > 0.0
            ? configuration_.soft_limit_positive - current_.position
            : current_.position - configuration_.soft_limit_negative;
        const auto braking_velocity = std::sqrt(2.0 * configuration_.maximum_deceleration
            * std::max(0.0, distance_to_limit));
        const auto desired_velocity = std::copysign(
            std::min(std::abs(commanded_velocity_), braking_velocity), commanded_velocity_);
        const auto rate = std::abs(desired_velocity) < std::abs(current_.velocity)
            ? configuration_.maximum_deceleration : configuration_.maximum_acceleration;
        current_.velocity = approach(current_.velocity, desired_velocity, rate);
        current_.position += current_.velocity * dt;
        if (current_.position <= configuration_.soft_limit_negative
            || current_.position >= configuration_.soft_limit_positive) {
            current_.position = std::clamp(current_.position,
                configuration_.soft_limit_negative, configuration_.soft_limit_positive);
            current_.velocity = 0.0;
            current_.mode = MotionSetpointMode::Hold;
        }
    } else if (current_.mode == MotionSetpointMode::Stopping) {
        current_.velocity = approach(current_.velocity, 0.0, configuration_.maximum_deceleration);
        current_.position = std::clamp(current_.position + current_.velocity * dt,
            configuration_.soft_limit_negative, configuration_.soft_limit_positive);
        if (current_.velocity == 0.0) current_.mode = MotionSetpointMode::Hold;
    }
    acceleration_=(current_.velocity-previous_velocity)/dt;
    return current_;
}

double MotionSetpointGenerator::approach(double value, double target, double rate) const noexcept {
    const auto step = rate * configuration_.cycle_seconds;
    if (value < target) return std::min(value + step, target);
    return std::max(value - step, target);
}

} // namespace ecmotion
