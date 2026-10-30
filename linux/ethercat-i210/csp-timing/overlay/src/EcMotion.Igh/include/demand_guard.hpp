// SPDX-License-Identifier: MIT
#pragma once
#include <array>
#include <cstdint>
#include <algorithm>
#include <cstdlib>
namespace ecmotion {
struct DemandResult {bool eligible{},ready{};unsigned fault{},excess{};std::int64_t error{};};
// Diagnostic shadow only; fixed bench units1048576 counts/rev,1ms. Never drives outputs.
class DemandGuard {
 std::array<std::int32_t,3> history{};unsigned size{},next{},excess{},fault{};
 std::uint64_t last_cycle{},last_time{};
public:
 DemandResult update(std::uint64_t cycle,std::uint64_t time,std::int32_t sent,std::int32_t demand,std::int32_t actual,std::int32_t drive_error,bool eligible,bool valid) noexcept {
  if(!eligible){*this=DemandGuard{};return {};}
  if(!valid || (size && (cycle<=last_cycle || cycle-last_cycle!=1 || time<=last_time || time-last_time!=1000000)))if(!fault)fault=1;
  const bool ready=size==3;
  if(ready && demand!=history[next] && !fault)fault=2;
  const auto error=std::max(std::llabs(static_cast<long long>(demand)-actual),std::llabs(static_cast<long long>(drive_error)));
  if(ready&&error>5242.88){if(excess<20)++excess;}else excess=0;
  if(excess>=20&&!fault)fault=3;
  history[next]=sent;next=(next+1)%3;if(size<3)++size;last_cycle=cycle;last_time=time;
  return {true,ready,fault,excess,error};
 }
};
}
