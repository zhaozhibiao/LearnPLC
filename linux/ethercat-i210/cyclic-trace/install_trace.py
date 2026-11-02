#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Apply the optional trace overlay to the exact verified diagnostic base.

No deployment/service/device operations. Refuses other base revisions and partial overlays.
"""
import argparse,hashlib,json,pathlib,shutil

def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def install(root):
    source=pathlib.Path(__file__).resolve().parent
    project=root/'src/EcMotion.Igh'
    main=project/'src/main.cpp';cmake=project/'CMakeLists.txt'
    expected={main:'584029510b1c91ca2f0d8d49265be37ada1458bb6a68a120f1c90780ed849283',
              cmake:'f05a29c1069b50fe99033dba110493904db52546ec730cf1f2afef48ddcc8d66'}
    for path,sha in expected.items():
        if digest(path)!=sha:raise RuntimeError('Trace overlay base hash mismatch: '+str(path))
    code=main.read_text()
    def replace(before,after):
        nonlocal code
        if code.count(before)!=1:raise RuntimeError('Trace integration anchor mismatch: '+before[:70])
        code=code.replace(before,after,1)
    replace('#include "axis_feedback.hpp"','#include "axis_feedback.hpp"\n#include "cyclic_trace.hpp"')
    replace('    std::string ethercat_config_path;','''    std::string ethercat_config_path;
    std::string axis_trace_path;
    std::string axis_trace_prefix="Axis1Drive";
    std::size_t axis_trace_capacity=60000;''')
    replace('        else if (std::strcmp(argv[index], "--socket") == 0)', '''        else if (std::strcmp(argv[index], "--axis-trace-csv") == 0) axis_trace_path=argv[index+1];
        else if (std::strcmp(argv[index], "--axis-trace-prefix") == 0) axis_trace_prefix=argv[index+1];
        else if (std::strcmp(argv[index], "--axis-trace-capacity") == 0) {
            const std::string value=argv[index+1];
            if(value.empty() || value.find_first_not_of("0123456789")!=std::string::npos)
                fail("trace capacity must be a positive integer");
            errno=0;char* end=nullptr;const auto count=std::strtoull(value.c_str(),&end,10);
            if(errno!=0 || !end || *end || count==0 || count>ecmotion::CyclicAxisTrace::maximum_capacity)
                fail("trace capacity must be 1..120000");
            axis_trace_capacity=static_cast<std::size_t>(count);
        }
        else if (std::strcmp(argv[index], "--socket") == 0)''')
    replace('    const auto bound_io = io_configuration.bind(pdo_layout);','''    std::unique_ptr<ecmotion::CyclicAxisTrace> axis_trace;
    if(!axis_trace_path.empty()) {
        if(axis_configurations.size()!=1) fail("axis trace currently requires exactly one configured axis");
        try {axis_trace=std::make_unique<ecmotion::CyclicAxisTrace>(pdo_layout,axis_trace_prefix,axis_trace_capacity);}
        catch(const std::exception& error) {fail(error.what());}
    }
    const auto bound_io = io_configuration.bind(pdo_layout);''')
    replace('        static_cast<void>(fieldbus_cycle.process(pdo_image, to_nanoseconds(actual_wakeup)));','''        if(axis_trace) {
            const auto previous_target=axis_trace->target_raw(pdo_image);
            const auto sample=fieldbus_cycle.process(pdo_image,to_nanoseconds(actual_wakeup));
            axis_trace->capture(pdo_image,sample.axes[0],
                diagnostics.cycles.load(std::memory_order_relaxed)+1,
                to_nanoseconds(wakeup),to_nanoseconds(actual_wakeup),realtime_bus_ready,previous_target);
        } else {
            static_cast<void>(fieldbus_cycle.process(pdo_image,to_nanoseconds(actual_wakeup)));
        }''')
    replace('    diagnostic_thread.join();','''    diagnostic_thread.join();
    if(axis_trace) {
        sched_param ordinary{};
        if(sched_setscheduler(0,SCHED_OTHER,&ordinary)!=0) fail("leave realtime scheduling before trace export failed");
        try {axis_trace->write_csv_exclusive(axis_trace_path);}
        catch(const std::exception& error) {fail(error.what());}
        std::fprintf(stderr,"ecmotion axis trace: rows=%zu dropped=%llu\\n",
            axis_trace->frames().size(),static_cast<unsigned long long>(axis_trace->dropped()));
    }''')
    addition='''
# Optional bounded cyclic diagnostic recorder. No control/trajectory changes.
add_library(ecmotion-cyclic-trace STATIC src/cyclic_trace.cpp)
target_include_directories(ecmotion-cyclic-trace PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-cyclic-trace PUBLIC ecmotion-pdo)
target_compile_options(ecmotion-cyclic-trace PRIVATE -Wall -Wextra -Wpedantic -Werror)
target_link_libraries(ecmotion-rt PRIVATE ecmotion-cyclic-trace)
if(BUILD_TESTING)
    add_executable(ecmotion-cyclic-trace-selftest src/cyclic_trace_selftest.cpp)
    target_link_libraries(ecmotion-cyclic-trace-selftest PRIVATE ecmotion-cyclic-trace)
    target_compile_options(ecmotion-cyclic-trace-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
    add_test(NAME cyclic-axis-trace COMMAND ecmotion-cyclic-trace-selftest)
endif()
'''
    targets=[(source/'cyclic_trace.hpp',project/'include/cyclic_trace.hpp'),
             (source/'cyclic_trace.cpp',project/'src/cyclic_trace.cpp'),
             (source/'cyclic_trace_selftest.cpp',project/'src/cyclic_trace_selftest.cpp')]
    for _,destination in targets:
        if destination.exists():raise RuntimeError('Refuse existing overlay file '+str(destination))
    old_cmake=cmake.read_text()
    # All anchors/hashes checked before mutating any target.
    for origin,destination in targets:shutil.copyfile(origin,destination)
    main.write_text(code);cmake.write_text(old_cmake+addition)
    paths=[main,cmake]+[destination for _,destination in targets]
    manifest={str(path.relative_to(root)):digest(path) for path in paths}
    (root/'cyclic-trace-overlay-manifest.json').write_text(json.dumps(manifest,indent=2))
    return manifest

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('root',type=pathlib.Path)
    args=parser.parse_args();print(json.dumps(install(args.root.resolve()),indent=2))
