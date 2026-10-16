// SPDX-License-Identifier: MIT
#include "communication_bridge.hpp"
#include <cerrno>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/un.h>
#include <unistd.h>
#include <fcntl.h>
namespace workbench_comm { namespace {
Mailbox<CommandPacket,64> commands;
Mailbox<FeedbackPacket,64> feedbacks;
std::atomic<bool> running{false},failed{false};
std::thread worker;
std::mutex lifecycle;
bool private_endpoint(const char* path) {
 if(!path || path[0]!='/' || std::strlen(path)>=sizeof(sockaddr_un::sun_path))return false;
 std::string endpoint(path);if(endpoint.find("/../")!=std::string::npos || endpoint.find("/./")!=std::string::npos)return false;
 auto slash=endpoint.rfind('/'); if(slash==0)return false;
 struct stat directory{},socket{};
 if(::lstat(endpoint.substr(0,slash).c_str(),&directory)!=0 || !S_ISDIR(directory.st_mode) || directory.st_uid!=::geteuid() || (directory.st_mode&0077))return false;
 if(::lstat(path,&socket)!=0 || !S_ISSOCK(socket.st_mode) || socket.st_uid!=::geteuid() || (socket.st_mode&0077))return false;
 return true;
}
bool transfer(int fd,void* data,std::size_t size,bool send) {
 auto p=static_cast<char*>(data);
 // Bound the entire frame, including partial transfers. A trickle of bytes cannot
 // extend the deadline indefinitely; no reconnect or replay follows failure.
 const auto deadline=std::chrono::steady_clock::now()+std::chrono::milliseconds(200);
 while(size && running.load(std::memory_order_acquire) && !failed.load(std::memory_order_acquire)) {
  const auto remaining=std::chrono::duration_cast<std::chrono::milliseconds>(deadline-std::chrono::steady_clock::now()).count();
  if(remaining<=0)return false;
  pollfd event{fd,static_cast<short>(send?POLLOUT:POLLIN),0};
  auto ready=::poll(&event,1,static_cast<int>(remaining));
  if(ready<0 && errno==EINTR)continue;
  if(ready<=0 || (event.revents&POLLNVAL))return false;
  auto n=send ? ::send(fd,p,size,MSG_NOSIGNAL) : ::recv(fd,p,size,0);
  if(n<0 && (errno==EINTR || errno==EAGAIN || errno==EWOULDBLOCK))continue;
  if(n<=0)return false;
  p+=n;size-=static_cast<std::size_t>(n);
 }
 return size==0;
}
void exchange(std::string endpoint) noexcept {
 int fd=-1;
 try {
 fd=::socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC,0);
 if(fd<0)throw 0;
 sockaddr_un address{};address.sun_family=AF_UNIX;std::memcpy(address.sun_path,endpoint.c_str(),endpoint.size()+1);
 if(::fcntl(fd,F_SETFL,O_NONBLOCK)!=0)throw 0;
 auto connected=::connect(fd,reinterpret_cast<sockaddr*>(&address),sizeof(address));
 if(connected!=0){ if(errno!=EINPROGRESS)throw 0; pollfd event{fd,POLLOUT,0};if(::poll(&event,1,50)<=0)throw 0;int error{};socklen_t length=sizeof(error);if(::getsockopt(fd,SOL_SOCKET,SO_ERROR,&error,&length)!=0 || error)throw 0; }
 // Verify peer after connect, closing the lstat/connect replacement window.
 ucred peer{};socklen_t length=sizeof(peer);
 if(::getsockopt(fd,SOL_SOCKET,SO_PEERCRED,&peer,&length)!=0 || peer.uid!=::geteuid())throw 0;
 while(running.load(std::memory_order_acquire) && !failed.load(std::memory_order_acquire)) {
  CommandPacket packet{};
  if(!commands.pop(packet)){std::this_thread::sleep_for(std::chrono::milliseconds(1));continue;}
  // One exchange per queued scan, in order. Distinct power/stop requests are never collapsed.
  FeedbackPacket result{};
  if(!transfer(fd,&packet,sizeof(packet),true) || !transfer(fd,&result,sizeof(result),false))throw 0;
  if(!feedbacks.push(result))throw 0; // explicit terminal overflow, never silent success
 }
 } catch(...) {failed.store(true,std::memory_order_release);}
 if(fd>=0)::close(fd); // disconnection makes worker stop/release its own lease
}
}
bool start(const char* endpoint) {
 std::lock_guard<std::mutex> guard(lifecycle);
 if(running.load() || worker.joinable())return false;
 try {
 const std::uint16_t endian=1;if(*reinterpret_cast<const unsigned char*>(&endian)!=1 || !private_endpoint(endpoint))return false;
 CommandPacket c{};FeedbackPacket f{};while(commands.pop(c)){}while(feedbacks.pop(f)){}
 failed.store(false);running.store(true,std::memory_order_release);
 worker=std::thread(exchange,std::string(endpoint));return true;
 } catch(...) {running.store(false,std::memory_order_release);failed.store(true,std::memory_order_release);return false;}
}
void stop(){std::lock_guard<std::mutex> guard(lifecycle);running.store(false,std::memory_order_release);if(worker.joinable())worker.join();}
bool submit(const CommandPacket& c) noexcept {
 if(!running.load(std::memory_order_acquire) || failed.load(std::memory_order_acquire))return false;
 if(commands.push(c))return true;
 failed.store(true,std::memory_order_release);return false;
}
bool poll(FeedbackPacket& f) noexcept {return !failed.load(std::memory_order_acquire) && feedbacks.pop(f);}
}
extern "C" bool workbench_comm_start(const char* s){return workbench_comm::start(s);}
extern "C" void workbench_comm_stop(){workbench_comm::stop();}




