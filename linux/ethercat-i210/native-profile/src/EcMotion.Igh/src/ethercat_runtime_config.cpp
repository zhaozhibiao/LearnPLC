#include "ethercat_runtime_config.hpp"
#include <nlohmann/json.hpp>
#include <algorithm>
#include <fstream>
#include <limits>
#include <set>
#include <stdexcept>
namespace ecmotion::runtime_config {
namespace {
using nlohmann::json;
constexpr std::size_t max_file=4U*1024U*1024U;
[[noreturn]] void bad(const std::string& why) { throw std::runtime_error("EtherCAT configuration: "+why); }
std::uint64_t number(const json& j,const char* key,std::uint64_t limit) {
 const auto& v=j.at(key); if(!v.is_number_integer() || (v.is_number_integer() && !v.is_number_unsigned() && v.get<std::int64_t>()<0)) bad(std::string(key)+" must be a nonnegative integer");
 const auto n=v.get<std::uint64_t>(); if(n>limit) bad(std::string(key)+" is out of range"); return n;
}
std::string text(const json& j,const char* key) { auto s=j.at(key).get<std::string>(); if(s.empty()||s.size()>512||s.find('\0')!=std::string::npos) bad(std::string(key)+" must be a bounded nonempty string");return s; }
const json& array(const json& j,const char* key,std::size_t max) { const auto& a=j.at(key);if(!a.is_array()||a.size()>max) bad(std::string(key)+" exceeds array bounds");return a; }
Direction direction(const json& j) {const auto s=text(j,"direction");if(s=="Rx") return Direction::Rx;if(s=="Tx") return Direction::Tx;bad("unknown PDO direction");}
}
Configuration Configuration::parse(std::string_view input) {
 if(input.size()>max_file) bad("file exceeds 4 MiB");
 const auto j=json::parse(input);
 if(number(j,"schemaVersion",1)!=1) bad("unsupported schemaVersion");
 if(!j.at("readyForRuntime").get<bool>()||!array(j,"blockers",4096).empty()) bad("topology has unresolved profile/readiness blockers");
 Configuration c; c.ready_for_runtime=true; c.cycle_time_ns=static_cast<std::uint32_t>(number(j,"cycleTimeNs",1000000000));
 if(c.cycle_time_ns<100000) bad("cycleTimeNs below 100 us");
 c.topology_fingerprint=text(j,"topologyFingerprint");
 if(j.contains("machineSha256")||j.contains("ioMapSha256")) {
  c.machine_sha256=text(j,"machineSha256");c.io_map_sha256=text(j,"ioMapSha256");
  for(const auto* hash:{&c.machine_sha256,&c.io_map_sha256}) if(hash->size()!=64||hash->find_first_not_of("0123456789abcdef")!=std::string::npos) bad("invalid SHA256 binding");
 }
 const auto& slaves=array(j,"slaves",64);if(slaves.empty()) bad("no slaves");
 std::set<std::uint16_t> positions;std::set<std::string> keys;std::size_t total_sdo_bytes=0;
 for(const auto& s:slaves) {
  Slave slave{};slave.position=static_cast<std::uint16_t>(number(s,"position",65535));
  if(!positions.insert(slave.position).second) bad("duplicate slave position");
  slave.vendor=static_cast<std::uint32_t>(number(s,"vendorId",UINT32_MAX));slave.product=static_cast<std::uint32_t>(number(s,"productCode",UINT32_MAX));slave.revision=static_cast<std::uint32_t>(number(s,"revisionNo",UINT32_MAX));slave.name=text(s,"name");
  const auto& dc=s.at("dc");slave.dc.enabled=dc.at("enabled").get<bool>();
  if(slave.dc.enabled) {
   slave.dc.assign_activate=static_cast<std::uint16_t>(number(dc,"assignActivate",65535));slave.dc.sync0_cycle_ns=static_cast<std::uint32_t>(number(dc,"sync0CycleNs",UINT32_MAX));
   const auto& shift=dc.at("sync0ShiftNs");if(!shift.is_number_integer()||shift.get<double>()<INT32_MIN||shift.get<double>()>INT32_MAX) bad("sync0ShiftNs out of range");slave.dc.sync0_shift_ns=shift.get<std::int32_t>();
   slave.dc.sync1_cycle_ns=static_cast<std::uint32_t>(number(dc,"sync1CycleNs",UINT32_MAX));
   if(slave.dc.sync0_cycle_ns!=c.cycle_time_ns) bad("DC sync0 cycle differs from runtime cycle");
  }
  slave.module_begin=c.module_idents.size();for(const auto& m:array(s,"moduleIdents",255)) {json v={{"v",m}};c.module_idents.push_back(static_cast<std::uint32_t>(number(v,"v",UINT32_MAX)));}slave.module_count=c.module_idents.size()-slave.module_begin;
  if(s.contains("syncManagers")) for(const auto& sm:array(s,"syncManagers",14)) {
   const auto index=static_cast<std::uint8_t>(number(sm,"index",15));if(index<2) bad("process data syncManager must be 2..15");
   if(std::any_of(slave.sync_managers.begin(),slave.sync_managers.end(),[&](const auto& v){return v.index==index;})) bad("duplicate syncManager");
   const auto wd=text(sm,"watchdog");if(wd!="Enable"&&wd!="Disable"&&wd!="Default") bad("unknown watchdog");
   slave.sync_managers.push_back({index,direction(sm),wd=="Enable"?Watchdog::Enable:wd=="Disable"?Watchdog::Disable:Watchdog::Default});
  } else {slave.sync_managers={{2,Direction::Rx,Watchdog::Enable},{3,Direction::Tx,Watchdog::Disable}};}
  std::set<std::pair<std::uint16_t,std::uint8_t>> objects;std::set<std::uint16_t> pdo_indexes;std::set<std::uint8_t> closed_syncs;int previous=-1;
  slave.pdo_begin=c.pdos.size();
  for(const auto& p:array(s,"pdos",1024)) {
   if(c.pdos.size()>=1024) bad("too many PDOs");
   Pdo pdo{direction(p),static_cast<std::uint8_t>(number(p,"syncManager",15)),static_cast<std::uint16_t>(number(p,"index",65535)),c.entries.size(),0};
   if(pdo.index==0||!pdo_indexes.insert(pdo.index).second) bad("zero or duplicate PDO index");
   const auto sm=std::find_if(slave.sync_managers.begin(),slave.sync_managers.end(),[&](const auto& v){return v.index==pdo.sync_manager;});
   if(sm==slave.sync_managers.end()||sm->direction!=pdo.direction) bad("PDO sync manager direction mismatch");
   if(previous!=pdo.sync_manager) {if(closed_syncs.count(pdo.sync_manager)) bad("PDOs for a syncManager must be contiguous");if(previous>=0) closed_syncs.insert(static_cast<std::uint8_t>(previous));previous=pdo.sync_manager;}
   const auto& entries=array(p,"entries",4096);if(entries.empty()) bad("empty PDO");
   for(const auto& e:entries) {
    if(c.entries.size()>=4096) bad("too many PDO entries");
    Entry entry{static_cast<std::uint16_t>(number(e,"index",65535)),static_cast<std::uint8_t>(number(e,"subindex",255)),static_cast<std::uint8_t>(number(e,"bitLength",64)),text(e,"key")};
    if(entry.index==0) bad("PDO padding is not supported by this runtime; remove mapping or block deployment");
    if(entry.bits==0) bad("zero bit length");
    if(!keys.insert(entry.key).second) bad("duplicate PDO key");
    if(!objects.insert({entry.index,entry.subindex}).second) bad("duplicate slave PDO object cannot be registered unambiguously");
    c.entries.push_back(std::move(entry));
   }
   pdo.entry_count=c.entries.size()-pdo.entry_begin;c.pdos.push_back(pdo);
  }
  slave.pdo_count=c.pdos.size()-slave.pdo_begin;if(slave.pdo_count==0) bad("slave has no PDOs");
  for(const auto& sd:array(s,"startupSdos",256)) {
   StartupSdo v{};v.index=static_cast<std::uint16_t>(number(sd,"index",65535));v.subindex=static_cast<std::uint8_t>(number(sd,"subindex",255));v.complete_access=sd.value("completeAccess",false);
   if(v.index==0x6040||v.index==0x607A||v.index==0x60FF||v.index==0x60B1||v.index==0x6071) bad("startup SDO may not write motion control/target objects");
   if(v.index==0 || (v.complete_access&&v.subindex!=0)) bad("invalid startup SDO index/completeAccess subindex");
   if(sd.contains("transition")&&text(sd,"transition")!="PS") bad("only PS startup SDO transition is supported");
   const auto& bytes=array(sd,"data",1024);if(bytes.empty()) bad("empty startup SDO");total_sdo_bytes+=bytes.size();if(total_sdo_bytes>65536) bad("aggregate startup SDO bytes exceeds 65536");
   for(const auto& b:bytes) {json item={{"v",b}};v.data.push_back(static_cast<std::uint8_t>(number(item,"v",255)));}slave.startup_sdos.push_back(std::move(v));
  }
  c.slaves.push_back(std::move(slave));
 }
 return c;
}
Configuration Configuration::load(const std::string& path) {
 std::ifstream stream(path,std::ios::binary|std::ios::ate);if(!stream) bad("cannot open "+path);const auto size=stream.tellg();if(size<0||static_cast<std::uint64_t>(size)>max_file) bad("file exceeds 4 MiB");
 std::string input(static_cast<std::size_t>(size),'\0');stream.seekg(0);if(!stream.read(input.data(),static_cast<std::streamsize>(input.size()))) bad("cannot read "+path);return parse(input);
}
}
