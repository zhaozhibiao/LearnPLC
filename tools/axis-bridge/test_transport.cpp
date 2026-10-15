#include "axis_bridge.hpp"
#include <cassert>
#include <cstdio>
#include <chrono>
#include <cstring>
#include <thread>
#include <string>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <unistd.h>
using namespace workbench_axis;
static bool transfer(int fd,void* data,std::size_t length,bool send){auto p=static_cast<char*>(data);while(length){auto n=send?::send(fd,p,length,MSG_NOSIGNAL) : ::recv(fd,p,length,0);if(n<=0)return false;p+=n;length-=n;}return true;}
int main(){
 assert(!start(nullptr));assert(!start("relative.sock"));assert(!start("/tmp/no-axis-test-socket"));
 char directory[]="/tmp/axis-native-test-XXXXXX";assert(::mkdtemp(directory));std::string endpoint=std::string(directory)+"/worker.sock";
 int listener=::socket(AF_UNIX,SOCK_STREAM,0);assert(listener>=0);sockaddr_un address{};address.sun_family=AF_UNIX;std::strcpy(address.sun_path,endpoint.c_str());assert(::bind(listener,reinterpret_cast<sockaddr*>(&address),sizeof(address))==0);assert(::chmod(endpoint.c_str(),0600)==0);assert(::listen(listener,1)==0);
  assert(::chmod(endpoint.c_str(),0644)==0);assert(!start(endpoint.c_str()));assert(::chmod(endpoint.c_str(),0600)==0);
 assert(::chmod(directory,0777)==0);assert(!start(endpoint.c_str()));assert(::chmod(directory,0700)==0);
 std::atomic<unsigned> received{0};
 std::thread server([&]{int client=::accept(listener,nullptr,nullptr);assert(client>=0);for(unsigned i=1;i<=3;++i){CommandPacket c{};if(!transfer(client,&c,sizeof(c),false))break;assert(c.axes[0].request==i);assert(c.axes[0].kind==(i==3?7:i));++received;FeedbackPacket f{};f.axes[0].request=i;std::this_thread::sleep_for(std::chrono::milliseconds(100));if(!transfer(client,&f,sizeof(f),true))break;}::close(client);});
 assert(start(endpoint.c_str()));assert(!start(endpoint.c_str()));
 for(unsigned i=1;i<=3;++i){CommandPacket c{};c.scan=i;c.flags=1;c.axes[0].request=i;c.axes[0].kind=i==3?7:i;assert(submit(c));}
 for(unsigned n=0;n<1000 && received.load()!=3;++n){std::this_thread::sleep_for(std::chrono::milliseconds(1));}assert(received==3);
 server.join();stop();stop(); // idempotent shutdown and no reconnect/replay
 // Queue overflow fails explicitly even while a connected worker is blocked.
 assert(::listen(listener,1)==0);
 std::thread blocked([&]{int fd=::accept(listener,nullptr,nullptr);assert(fd>=0);std::this_thread::sleep_for(std::chrono::milliseconds(150));::close(fd);});
 assert(start(endpoint.c_str()));bool overflow=false;for(unsigned i=0;i<10000;++i){CommandPacket c{};c.scan=i+1;if(!submit(c)){overflow=true;break;}}assert(overflow);stop();blocked.join();
 // A 1ms producer may queue 100 scans during a legitimate Home receipt wait.
 std::atomic<unsigned> burstReceived{0};
 std::thread burst([&]{int fd=::accept(listener,nullptr,nullptr);assert(fd>=0);for(unsigned i=1;i<=120;++i){CommandPacket c{};if(!transfer(fd,&c,sizeof(c),false))break;assert(c.scan==i && c.axes[0].request==i);if(i==1)std::this_thread::sleep_for(std::chrono::milliseconds(100));FeedbackPacket f{};f.axes[0].request=i;if(!transfer(fd,&f,sizeof(f),true))break;++burstReceived;}::close(fd);});
 assert(start(endpoint.c_str()));for(unsigned i=1;i<=120;++i){CommandPacket c{};c.scan=i;c.flags=1;c.axes[0].request=i;c.axes[0].kind=4;assert(submit(c));std::this_thread::sleep_for(std::chrono::milliseconds(1));}
 for(unsigned n=0;n<2000 && burstReceived.load()!=120;++n){std::this_thread::sleep_for(std::chrono::milliseconds(1));}assert(burstReceived==120);burst.join();
 FeedbackPacket burstFeedback{};unsigned publications=0;while(poll(burstFeedback)){++publications;assert(burstFeedback.axes[0].request==publications);}assert(publications==120);stop();
 // A partial feedback frame must not renew the 200ms frame deadline.
 std::thread trickle([&]{int fd=::accept(listener,nullptr,nullptr);assert(fd>=0);CommandPacket c{};assert(transfer(fd,&c,sizeof(c),false));char byte=0;for(unsigned i=0;i<5;++i){if(::send(fd,&byte,1,MSG_NOSIGNAL)!=1)break;std::this_thread::sleep_for(std::chrono::milliseconds(80));}::close(fd);});
 assert(start(endpoint.c_str()));CommandPacket slow{};slow.scan=1;assert(submit(slow));
 std::this_thread::sleep_for(std::chrono::milliseconds(300));assert(!submit(slow));
 stop();trickle.join();
 std::puts("ordered delayed replies, private permissions, overflow and 200ms whole-frame deadline passed");
 ::close(listener);::unlink(endpoint.c_str());::rmdir(directory);
}







