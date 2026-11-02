// MIT. Read-only fixed-frame bridge probe: every command request stays zero.
#include "axis_bridge.hpp"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <iostream>
#include <thread>
#include <vector>
int main(int argc,char** argv){
 using namespace workbench_axis;using clock=std::chrono::steady_clock;
 if(argc!=3){std::cerr<<"Usage: axis-bridge-probe <isolated-private-socket> <2..1000 samples>\n";return 2;}
 int count;
 try{count=std::stoi(argv[2]);}catch(...){return 2;}
 if(count<2 || count>1000)return 2;
 if(!start(argv[1])){std::cerr<<"Private probe channel unavailable\n";return 1;}
 std::vector<double> times;bool valid=true;
 for(int n=0;n<count && valid;++n){
  // No request/kind/position/velocity is ever populated; scan liveness only.
  CommandPacket image{};image.scan=static_cast<std::uint64_t>(n+1);image.flags=1;
  const auto begin=clock::now();
  if(!submit(image)){valid=false;break;}
  FeedbackPacket result{};bool received=false;
  while(clock::now()-begin<std::chrono::milliseconds(300)){
   if(poll(result)){received=true;break;}
   std::this_thread::sleep_for(std::chrono::microseconds(50));
  }
  if(!received){valid=false;break;}
  for(const auto& slot:result.axes)
   if(slot.request || slot.busy || slot.done || slot.error || slot.aborted || slot.rejected_request || slot.rejected_error)valid=false;
  times.push_back(std::chrono::duration<double,std::milli>(clock::now()-begin).count());
  std::this_thread::sleep_until(begin+std::chrono::milliseconds(10));
 }
 stop();
 if(!valid || times.size()!=static_cast<std::size_t>(count)){std::cerr<<"Probe failed; no motion request was sent\n";return 1;}
 std::sort(times.begin(),times.end());double total=0;for(auto value:times)total+=value;
 auto percentile=[&](double q){return times[static_cast<std::size_t>(std::ceil(q*count))-1];};
 std::cout<<"{\"Status\":\"PASS\",\"Measurement\":\"zero_request_cpp_python_frame_round_trip\",\"Samples\":"<<count
  <<",\"MeanMilliseconds\":"<<total/count<<",\"P95Milliseconds\":"<<percentile(.95)
  <<",\"P99Milliseconds\":"<<percentile(.99)<<",\"MaxMilliseconds\":"<<times.back()<<"}\n";
}
