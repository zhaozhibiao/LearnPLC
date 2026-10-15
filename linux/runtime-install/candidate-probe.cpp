// Isolated counter-package smoke test, using the pinned Runtime's ABI mirror.
// This is a test executable, not an activation implementation or PLC scheduler.
#include "strucpp_abi.hpp"
#include <dlfcn.h>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <stdexcept>

static void require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
template<class T> static T symbol(void* library, const char* name) {
    auto result = dlsym(library,name);
    if (!result) throw std::runtime_error(std::string("Missing export: ")+name);
    return reinterpret_cast<T>(result);
}
int main(int argc, char** argv) {
    try {
        require(argc==3,"Usage: candidate-probe /candidate/new_libplc.so expected_program_md5");
        void* library=dlopen(argv[1],RTLD_NOW|RTLD_LOCAL);
        if (!library) throw std::runtime_error(dlerror());
        for (auto name : {"strucpp_advance_time","strucpp_set_current_time","strucpp_get_config",
             "strucpp_get_located_vars","strucpp_get_located_var_count","strucpp_debug_array_count",
             "strucpp_debug_elem_count","strucpp_debug_size","strucpp_debug_set",
             "strucpp_debug_read","strucpp_debug_write"}) symbol<void*>(library,name);
        auto md5=symbol<const char*>(library,"strucpp_program_md5");
        require(std::strlen(argv[2])==32 && std::memcmp(md5,argv[2],32)==0 && md5[32]=='\0',"Program identity mismatch");
        auto getConfig=symbol<strucpp::ConfigurationInstance*(*)()>(library,"strucpp_get_config");
        auto locatedCount=symbol<uint32_t(*)()>(library,"strucpp_get_located_var_count");
        auto arrays=symbol<uint8_t(*)()>(library,"strucpp_debug_array_count");
        auto elements=symbol<uint16_t(*)(uint8_t)>(library,"strucpp_debug_elem_count");
        auto size=symbol<uint16_t(*)(uint8_t,uint16_t)>(library,"strucpp_debug_size");
        auto read=symbol<uint16_t(*)(uint8_t,uint16_t,uint8_t*)>(library,"strucpp_debug_read");
        auto config=getConfig();
        require(config && std::strcmp(config->get_name(),"CONFIG0")==0,"Configuration ABI mismatch");
        require(config->get_resource_count()==1 && locatedCount()==0,"Unexpected resources or hardware I/O");
        auto resources=config->get_resources();
        require(resources && std::strcmp(resources[0].name,"RES0")==0 && resources[0].task_count==1,"Resource ABI mismatch");
        auto task=&resources[0].tasks[0];
        require(task->interval_ns==10000000 && task->program_count==1 && task->programs && task->programs[0],"Task ABI mismatch");
        require(arrays()==1 && elements(0)==1 && size(0,0)==4,"Debug table ABI mismatch");
        uint8_t bytes[8]{};
        require(read(0,0,bytes)==4,"Initial debug read failed");
        int32_t initial{}; std::memcpy(&initial,bytes,4);
        for(int i=0;i<10;++i) task->programs[0]->run();
        require(read(0,0,bytes)==4,"Final debug read failed");
        int32_t final{}; std::memcpy(&final,bytes,4);
        require(final==initial+10,"Compiled counter did not advance exactly ten calls");
        std::cout<<"PASS pinned Runtime ABI + required exports + debug read + counter "<<initial<<" -> "<<final<<"\n";
        require(dlclose(library)==0,"dlclose failed");
        return 0;
    } catch(const std::exception& error) { std::cerr<<"FAIL "<<error.what()<<"\n"; return 1; }
}
