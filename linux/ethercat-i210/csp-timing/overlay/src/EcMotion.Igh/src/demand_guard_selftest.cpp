#include "demand_guard.hpp"
#include <stdexcept>
#include <iostream>
void check(bool b){if(!b)throw std::runtime_error("demand guard regression");}
int main(){
 for(int kind=0;kind<6;++kind){ecmotion::DemandGuard g;ecmotion::DemandResult r;
 for(int i=1;i<=100;++i)r=g.update(i,i*1000000ULL,i*1000,(i-(kind==1?4:3))*1000,kind==2?0:(i-3)*1000,kind==3?6000:0,true,!(kind==4&&i==20));
 check(r.fault==(kind==1?2:kind==2||kind==3?3:kind==4?1:0));
 r=g.update(101,101000000,0,999,0,0,false,true);check(!r.ready&&r.fault==0);
 r=g.update(102,102000000,0,0,0,0,true,true);check(!r.ready&&r.fault==0);
 }
 ecmotion::DemandGuard g;g.update(1,1000000,1,0,0,0,true,true);auto r=g.update(3,3000000,2,0,0,0,true,true);check(r.fault==1);
 std::cout<<"demand guard PASS\n";
}
