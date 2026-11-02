#include "ethercat_runtime_config.hpp"
#include <nlohmann/json.hpp>
#include <iostream>
#include <stdexcept>
using nlohmann::json;
using ecmotion::runtime_config::Configuration;
json fixture() {
 return json::parse(R"({"schemaVersion":1,"readyForRuntime":true,"blockers":[],"cycleTimeNs":1000000,"topologyFingerprint":"test-topology","slaves":[{"position":0,"vendorId":1,"productCode":2,"revisionNo":3,"name":"Drive","dc":{"enabled":false},"moduleIdents":[],"startupSdos":[],"pdos":[{"direction":"Rx","syncManager":2,"index":5632,"entries":[{"index":24698,"subindex":0,"bitLength":32,"key":"Drive.Target"}]},{"direction":"Tx","syncManager":3,"index":6656,"entries":[{"index":24676,"subindex":0,"bitLength":32,"key":"Drive.Actual"}]}]}]})");
}
void require(bool condition,const char* message) { if(!condition) throw std::runtime_error(message); }
void reject(const json& value) { bool failed=false; try { (void)Configuration::parse(value.dump()); } catch(const std::exception&) { failed=true; } require(failed,"unsafe config accepted"); }
int main() { try {
 auto j=fixture(); auto c=Configuration::parse(j.dump()); require(c.slaves.size()==1 && c.entries.size()==2 && c.entries[0].key=="Drive.Target","valid config lost");
 auto copied=c; c={}; require(copied.entries[0].key=="Drive.Target","owned key lifetime");
 j=fixture(); j["readyForRuntime"]=false; reject(j);
 j=fixture(); j["blockers"]={"unknown profile"}; reject(j);
 j=fixture(); j["schemaVersion"]=2; reject(j);
 j=fixture(); j["slaves"][0]["position"]=-1; reject(j);
 j=fixture(); j["slaves"][0]["revisionNo"]=4294967296ULL; reject(j);
 j=fixture(); j["slaves"][0]["pdos"][0]["entries"][0]["bitLength"]=0; reject(j);
 j=fixture(); j["slaves"][0]["pdos"][0]["entries"][0]["index"]=0; reject(j);
 j=fixture(); j["slaves"].push_back(j["slaves"][0]); reject(j);
 j=fixture(); j["slaves"][0]["pdos"][1]["entries"][0]["key"]="Drive.Target"; reject(j);
 j=fixture(); j["slaves"][0]["pdos"][0]["direction"]="invalid"; reject(j);
 j=fixture(); j["slaves"][0]["pdos"][0]["syncManager"]=4; reject(j);
 j=fixture(); j["slaves"][0]["syncManagers"]={{{"index",2},{"direction","Tx"},{"watchdog","Enable"}},{{"index",3},{"direction","Tx"},{"watchdog","Disable"}}}; reject(j);
 j=fixture(); j["slaves"][0]["startupSdos"]={{{"index",8192},{"subindex",1},{"completeAccess",true},{"data",{1}}}}; reject(j);
 j=fixture(); j["slaves"][0]["startupSdos"]={{{"index",8192},{"subindex",0},{"transition","OP"},{"data",{1}}}}; reject(j);
 j=fixture(); j["slaves"][0]["startupSdos"]={{{"index",8192},{"subindex",0},{"data",{1,2,3}}}}; c=Configuration::parse(j.dump()); require(c.slaves[0].startup_sdos[0].data.size()==3,"SDO bytes lost");
 j=fixture(); auto slave=j["slaves"][0]; j["slaves"]=json::array(); for(int i=0;i<65;++i) {slave["position"]=i;j["slaves"].push_back(slave);} reject(j);
 j=fixture(); j["slaves"][0]["startupSdos"]={{{"index",24640},{"subindex",0},{"data",{1,0}}}}; reject(j);
 j=fixture(); j["machineSha256"]="bad"; reject(j);
 j=fixture(); j["machineSha256"]=std::string(64,'a');j["ioMapSha256"]=std::string(64,'b');c=Configuration::parse(j.dump()); require(c.machine_sha256==std::string(64,'a'),"machine hash lost");
 j=fixture(); j["slaves"][0]["syncManagers"]={{{"index",4},{"direction","Rx"},{"watchdog","Default"}},{{"index",5},{"direction","Tx"},{"watchdog","Disable"}}};j["slaves"][0]["pdos"][0]["syncManager"]=4;j["slaves"][0]["pdos"][1]["syncManager"]=5;c=Configuration::parse(j.dump());require(c.slaves[0].sync_managers[0].index==4,"explicit sync manager lost");
 j=fixture(); j["slaves"][0]["dc"]={{"enabled",true},{"assignActivate",768},{"sync0CycleNs",2000000},{"sync0ShiftNs",0},{"sync1CycleNs",0}};reject(j);
 j=fixture(); j["slaves"][0]["startupSdos"]={{{"index",8192},{"subindex",0},{"data",json::array()}}};reject(j);
 j=fixture();j["slaves"][0]["pdos"][0]["entries"][0]["bitLength"]=64.0;reject(j);
 std::cout<<"runtime config selftest passed\n"; return 0;
 } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';return 1;}}
