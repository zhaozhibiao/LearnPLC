// Exercise the production JSON dispatcher without adding a public testing API.
#include "native_protocol_server.cpp"
#include "fieldbus_control_backend.hpp"
#include "fieldbus_cycle.hpp"
#include "mailbox_control_backend.hpp"
#include "motion_setpoint.hpp"
#include <filesystem>
#include <iostream>

namespace {
using namespace ecmotion;
void check(bool ok,const char* why) {if(!ok) throw std::runtime_error(why);}
struct Axis final : RealtimeAxisAdapter {
    RealtimeCommand last{};
    unsigned applied{};
    bool valid(const RealtimeCommand& c) const noexcept {
        MotionSetpointGenerator g({.001,-25,25,2,2,3,20});g.synchronize(0);
        const bool v2=c.motion_profile_version==2;
        if(c.kind==RealtimeCommandKind::MoveAbsolute)
            return g.move_absolute(c.position,c.velocity,v2?c.acceleration:0,v2?c.deceleration:0,v2?c.jerk:0,v2);
        if(c.kind==RealtimeCommandKind::Jog)
            return v2?g.jog_profile(c.velocity,c.acceleration,c.deceleration,c.jerk):g.jog(c.velocity);
        if(c.kind==RealtimeCommandKind::Stop)
            return !v2 || g.stop_profile(c.deceleration,c.jerk);
        return false;
    }
    bool can_apply_command(const RealtimeCommand& c) const noexcept override {return valid(c);}
    bool apply_command(const RealtimeCommand& c) noexcept override {
        if(!valid(c)) return false;
        last=c;++applied;return true;
    }
    void apply_safe_state() noexcept override {}
    void process(PdoImageView&,RealtimeAxisSnapshot& s) noexcept override {
        s.state=RealtimeAxisState::Standstill;s.homed=true;s.motion_timing_ready=true;
        s.motion_id=last.sequence;s.motion_target=last.position;
    }
};
}
int main(int argc,char** argv) {try {
    using namespace ecmotion;
    check(argc==2,"private path fixture required");
    std::filesystem::create_directories(argv[1]);PathResourceStore paths(argv[1]);
    PdoLayout layout({{"fixture",PdoDirection::ControllerInput,0,0,8}},1);
    std::vector<std::uint8_t> bytes(1);PdoImageView image(bytes,layout);BoundIoMap map;PdoIoCycle io(map,image);
    auto exchange=std::make_unique<RealtimeExchange>();Axis axis;
    FieldbusCycle cycle(*exchange,io,{&axis});cycle.set_bus_ready(true);
    FieldbusControlBackend backend(*exchange,{}, {"X"});backend.set_bus_ready(true);
    std::jthread ticker([&](std::stop_token stop){while(!stop.stop_requested()) {
        const auto now=static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
        static_cast<void>(cycle.process(image,now));std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }});
    for(unsigned n=0;n<1000 && !backend.runtime_ready();++n)std::this_thread::sleep_for(std::chrono::milliseconds(1));
    check(backend.runtime_ready(),"fixture realtime startup");
    std::uint64_t sequence=0,scan=0,action_id=0;
    auto call=[&](ControlBackend& target,const char* kind,json payload) {
        json req{{"protocolVersion",4},{"requestId","profile-"+std::to_string(++sequence)},
            {"sequence",sequence},{"kind",kind},{"payload",std::move(payload)}};
        return dispatch(json::parse(req.dump()),101,target,"ProfileTest",paths);
    };
    auto hello=call(backend,"HelloRequest",{{"clientName","test"}});
    const auto caps=hello.at("payload").at("runtimeCapabilities");
    check(std::find(caps.begin(),caps.end(),"workbench_axis_move_profile_v2")!=caps.end(),"missing move capability");
    check(std::find(caps.begin(),caps.end(),"workbench_axis_velocity_profile_v2")!=caps.end(),"missing velocity capability");
    auto acquired=call(backend,"AcquireControlRequest",{{"clientName","test"},{"requestedLeaseMilliseconds",30000}});
    const auto token=acquired.at("payload").at("leaseToken");
    auto direct=[&](json p) {p["axis"]="X";p["leaseToken"]=token;return call(backend,"AxisCommandRequest",std::move(p));};
    auto batch=[&](json a) {
        a["requestId"]=++action_id;a["axis"]=0;a["outputMask"]=0;a["outputValues"]=0;a["groupCount"]=2;
        if(!a.contains("position"))a["position"]=0;
        if(!a.contains("velocity"))a["velocity"]=0;
        if(!a.contains("acceleration"))a["acceleration"]=0;
        if(!a.contains("deceleration"))a["deceleration"]=0;
        const auto now=static_cast<std::uint64_t>(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
        return call(backend,"PlcScanBatchRequest",{{"leaseToken",token},{"sessionId",1},{"scanIndex",++scan},
            {"deadlineNs",now+90000000},{"sourceCycle",backend.sample_stamp().first},{"axes",{"X"}},
            {"outputs",json::array()},{"actions",json::array({a})}});
    };
    auto reject=[&](auto operation,const char* why) {
        const auto before=axis.applied;bool rejected=false;
        try {static_cast<void>(operation());}catch(const std::exception&){rejected=true;}
        check(rejected && axis.applied==before,why);
    };
    const json move{{"command","MoveAbsolute"},{"position",.1},{"velocity",.2},{"motionProfileVersion",2},
        {"acceleration",.5},{"deceleration",.75},{"jerk",2}};
    check(direct(move).at("payload").at("accepted")==true,"direct move rejected");
    check(axis.last.motion_profile_version==2 && axis.last.acceleration==.5 && axis.last.deceleration==.75 && axis.last.jerk==2,"direct profile lost");
    json action=move;action.erase("command");action["kind"]=4;
    auto receipt=batch(action).at("payload").at("actionReceipts").at(0);
    check(receipt.at("accepted")==true && receipt.at("motionId").get<std::uint64_t>()>0,"batch receipt lost");
    check(axis.last.motion_profile_version==2 && axis.last.acceleration==.5 && axis.last.deceleration==.75 && axis.last.jerk==2,"batch profile lost");
    for(const auto& field:{"acceleration","deceleration","jerk"}) {
        for(double bad:{-1.0,100.0}) {auto d=move;d[field]=bad;reject([&]{return direct(d);},"direct bad profile applied");
            auto a=action;a[field]=bad;reject([&]{return batch(a);},"batch bad profile applied");}
        auto d=move;d[field]=nullptr;reject([&]{return direct(d);},"direct nonnumeric profile applied");
        auto a=action;a[field]=nullptr;reject([&]{return batch(a);},"batch nonnumeric profile applied");
    }
    for(const json& version:{json(1),json(3),json(2.5),json("2"),json(nullptr)}) {
        auto d=move;d["motionProfileVersion"]=version;reject([&]{return direct(d);},"direct bad version applied");
        auto a=action;a["motionProfileVersion"]=version;reject([&]{return batch(a);},"batch bad version applied");
    }
    auto omitted=move;omitted.erase("acceleration");omitted.erase("deceleration");omitted.erase("jerk");
    check(direct(omitted).at("payload").at("accepted")==true && axis.last.acceleration==0 && axis.last.deceleration==0 && axis.last.jerk==0,"direct zero inheritance lost");
    auto legacy=move;legacy.erase("motionProfileVersion");legacy["acceleration"]=-1;
    check(direct(legacy).at("payload").at("accepted")==true && axis.last.motion_profile_version==0,"direct legacy fallback changed");
    auto old_action=action;old_action.erase("motionProfileVersion");old_action["acceleration"]=-1;
    check(batch(old_action).at("payload").at("accepted")==true && axis.last.motion_profile_version==0,"batch legacy fallback changed");
    for(const auto& entry:std::array<std::pair<const char*,int>,2>{{{"Jog",11},{"Stop",5}}}) {
        auto d=move;d["command"]=entry.first;if(entry.second==5)d["acceleration"]=0;
        check(direct(d).at("payload").at("accepted")==true && axis.last.motion_profile_version==2,"direct velocity profile lost");
        auto a=action;a["kind"]=entry.second;if(entry.second==5)a["acceleration"]=0;
        check(batch(a).at("payload").at("accepted")==true && axis.last.motion_profile_version==2,"batch velocity profile lost");
    }
    MailboxControlBackend simulator;
    const auto simhello=call(simulator,"HelloRequest",{{"clientName","test"}}).at("payload").at("runtimeCapabilities");
    check(std::find(simhello.begin(),simhello.end(),"workbench_axis_move_profile_v2")==simhello.end()
        && std::find(simhello.begin(),simhello.end(),"workbench_axis_velocity_profile_v2")==simhello.end(),"simulator falsely advertises v2");
    auto unsupported=move;unsupported["axis"]="Axis1";unsupported["leaseToken"]=token;
    reject([&]{return call(simulator,"AxisCommandRequest",unsupported);},"simulator accepted unsupported profile");
    for(const auto* invalid_json:{"{\"acceleration\":NaN}","{\"deceleration\":Infinity}","{\"jerk\":1e999}"})
        reject([&]{return json::parse(invalid_json);},"nonfinite/overflow JSON parsed");
    ticker.request_stop();std::cout<<"native profile JSON dispatch PASS\n";return 0;
} catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}}
