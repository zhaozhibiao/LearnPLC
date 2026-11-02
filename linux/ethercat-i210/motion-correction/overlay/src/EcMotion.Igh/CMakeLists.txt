cmake_minimum_required(VERSION 3.20)
project(ecmotion_rt VERSION 0.1.0 LANGUAGES C CXX)
include(CTest)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(ECMOTION_IGH_ROOT "" CACHE PATH "IgH userspace installation prefix (contains include/ecrt.h and lib/libethercat.so)")
if(ECMOTION_IGH_ROOT)
    find_path(IGH_INCLUDE_DIR ecrt.h HINTS "${ECMOTION_IGH_ROOT}/include" NO_DEFAULT_PATH)
    find_library(IGH_LIBRARY ethercat HINTS "${ECMOTION_IGH_ROOT}/lib" "${ECMOTION_IGH_ROOT}/lib64" NO_DEFAULT_PATH)
else()
    find_path(IGH_INCLUDE_DIR ecrt.h)
    find_library(IGH_LIBRARY ethercat)
endif()
if(NOT IGH_INCLUDE_DIR OR NOT IGH_LIBRARY)
    message(FATAL_ERROR "IgH userspace development files not found. Set -DECMOTION_IGH_ROOT=/path/to/etherlab (or IGH_INCLUDE_DIR and IGH_LIBRARY). Do not use an unrelated ecrt.h: header and libethercat must be from the same IgH build.")
endif()
find_package(nlohmann_json 3.2.0 REQUIRED)
add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/../EcMotion.SoftPlc"
    "${CMAKE_CURRENT_BINARY_DIR}/softplc")

add_library(ecmotion-pdo STATIC src/pdo_image.cpp src/logical_io.cpp)
target_include_directories(ecmotion-pdo PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_options(ecmotion-pdo PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-io-config STATIC src/io_map_config.cpp)
target_include_directories(ecmotion-io-config PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-io-config PUBLIC ecmotion-pdo nlohmann_json::nlohmann_json)
target_compile_options(ecmotion-io-config PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-exchange STATIC src/realtime_exchange.cpp)
target_include_directories(ecmotion-exchange PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_options(ecmotion-exchange PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-axis-control STATIC
    src/axis_feedback.cpp src/motion_setpoint.cpp src/motion_safety.cpp)
target_include_directories(ecmotion-axis-control PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-axis-control PUBLIC ecmotion-pdo ecmotion-exchange nlohmann_json::nlohmann_json)
target_compile_options(ecmotion-axis-control PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-native-protocol STATIC src/native_protocol_server.cpp src/control_backend.cpp src/path_resource.cpp)
target_include_directories(ecmotion-native-protocol PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-native-protocol PUBLIC nlohmann_json::nlohmann_json)
target_compile_options(ecmotion-native-protocol PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-fieldbus-backend STATIC src/fieldbus_control_backend.cpp)
target_include_directories(ecmotion-fieldbus-backend PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-fieldbus-backend PUBLIC ecmotion-exchange ecmotion-native-protocol)
target_compile_options(ecmotion-fieldbus-backend PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_library(ecmotion-io-cycle STATIC src/pdo_io_cycle.cpp src/fieldbus_cycle.cpp src/group_interpolator.cpp)
target_include_directories(ecmotion-io-cycle PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-io-cycle PUBLIC ecmotion-io-config ecmotion-axis-control ecmotion-exchange)
target_compile_options(ecmotion-io-cycle PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_executable(ecmotion-rt src/main.cpp)
option(ECMOTION_IS620N_PROFILE "Use the evidence-derived single IS620N commissioning profile" OFF)
if(ECMOTION_IS620N_PROFILE)
    target_compile_definitions(ecmotion-rt PRIVATE ECMOTION_IS620N_PROFILE=1)
endif()
target_include_directories(ecmotion-rt PRIVATE "${IGH_INCLUDE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/generated")
target_link_libraries(ecmotion-rt PRIVATE
    ecmotion-io-cycle ecmotion-axis-control ecmotion-native-protocol ecmotion-fieldbus-backend
    "${IGH_LIBRARY}" pthread rt)
target_compile_options(ecmotion-rt PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_executable(ecmotion-native-sim
    src/native_sim_main.cpp
    src/mailbox_control_backend.cpp
    src/virtual_control_backend.cpp
    src/virtual_group_backend.cpp)
target_include_directories(ecmotion-native-sim PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-native-sim PRIVATE ecmotion-exchange ecmotion-native-protocol ecmotion-io-cycle pthread)
target_compile_options(ecmotion-native-sim PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_executable(ecmotion-pdo-selftest src/pdo_image_selftest.cpp)
target_link_libraries(ecmotion-pdo-selftest PRIVATE ecmotion-pdo)
target_compile_options(ecmotion-pdo-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME generic-pdo-image COMMAND ecmotion-pdo-selftest)
endif()

add_executable(ecmotion-io-map-selftest src/io_map_config_selftest.cpp)
target_include_directories(ecmotion-io-map-selftest PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/generated")
target_link_libraries(ecmotion-io-map-selftest PRIVATE
    ecmotion-io-cycle ecmotion-axis-control ecmotion-fieldbus-backend)
target_compile_options(ecmotion-io-map-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME native-io-map
        COMMAND ecmotion-io-map-selftest
            "${CMAKE_CURRENT_SOURCE_DIR}/../../config/io-map.json"
            "${CMAKE_CURRENT_SOURCE_DIR}/../../config/reference-machine.json")
endif()

add_executable(ecmotion-motion-selftest src/motion_setpoint_selftest.cpp)
target_link_libraries(ecmotion-motion-selftest PRIVATE ecmotion-axis-control)
target_compile_options(ecmotion-motion-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME motion-setpoint COMMAND ecmotion-motion-selftest)
endif()

add_executable(ecmotion-slave-identity-selftest src/slave_identity_selftest.cpp)
target_include_directories(ecmotion-slave-identity-selftest PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_options(ecmotion-slave-identity-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME slave-identity-startup-retry COMMAND ecmotion-slave-identity-selftest)
endif()

add_executable(ecmotion-group-selftest src/group_interpolator_selftest.cpp)
target_link_libraries(ecmotion-group-selftest PRIVATE ecmotion-io-cycle)
target_compile_options(ecmotion-group-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME group-interpolator COMMAND ecmotion-group-selftest)
endif()

if(BUILD_TESTING)
    add_executable(ecmotion-path-selftest src/path_resource_selftest.cpp)
    target_link_libraries(ecmotion-path-selftest PRIVATE ecmotion-io-cycle ecmotion-fieldbus-backend ecmotion-native-protocol pthread)
    target_compile_options(ecmotion-path-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
    add_test(NAME autonomous-path-resources COMMAND ecmotion-path-selftest "${CMAKE_CURRENT_BINARY_DIR}/path-test-store")
endif()

# Standalone geometry/planning kernel; deliberately not linked into the live cycle yet.
add_library(ecmotion-stroke STATIC src/bezier_stroke.cpp)
target_include_directories(ecmotion-stroke PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_options(ecmotion-stroke PRIVATE -Wall -Wextra -Wpedantic -Werror)

add_executable(ecmotion-stroke-selftest src/bezier_stroke_selftest.cpp)
target_link_libraries(ecmotion-stroke-selftest PRIVATE ecmotion-stroke)
target_compile_options(ecmotion-stroke-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
if(BUILD_TESTING)
    add_test(NAME bezier-stroke COMMAND ecmotion-stroke-selftest)
endif()

install(TARGETS ecmotion-rt ecmotion-native-sim RUNTIME DESTINATION bin)
if(BUILD_TESTING)
    add_executable(ecmotion-is620n-profile-selftest "${CMAKE_CURRENT_SOURCE_DIR}/../../is620n_profile_selftest.cpp")
    target_include_directories(ecmotion-is620n-profile-selftest PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/generated")
    target_link_libraries(ecmotion-is620n-profile-selftest PRIVATE ecmotion-io-cycle ecmotion-axis-control)
    add_test(NAME is620n-profile COMMAND ecmotion-is620n-profile-selftest
        "${CMAKE_CURRENT_SOURCE_DIR}/../../config/is620n-machine.json"
        "${CMAKE_CURRENT_SOURCE_DIR}/../../config/is620n-io-map.json")
endif()
if(BUILD_TESTING)
    add_executable(ecmotion-typed-io-sim src/typed_io_sim_main.cpp)
    target_include_directories(ecmotion-typed-io-sim PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/generated")
    target_link_libraries(ecmotion-typed-io-sim PRIVATE ecmotion-io-cycle ecmotion-fieldbus-backend pthread)
    target_compile_options(ecmotion-typed-io-sim PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

add_library(ecmotion-ethercat-config STATIC src/ethercat_runtime_config.cpp)
target_include_directories(ecmotion-ethercat-config PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(ecmotion-ethercat-config PUBLIC nlohmann_json::nlohmann_json)
target_compile_options(ecmotion-ethercat-config PRIVATE -Wall -Wextra -Wpedantic -Werror)
target_link_libraries(ecmotion-rt PRIVATE ecmotion-ethercat-config)
if(BUILD_TESTING)
 add_executable(ecmotion-ethercat-config-selftest src/ethercat_runtime_config_selftest.cpp)
 target_link_libraries(ecmotion-ethercat-config-selftest PRIVATE ecmotion-ethercat-config)
 target_compile_options(ecmotion-ethercat-config-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
 add_test(NAME ethercat-runtime-config COMMAND ecmotion-ethercat-config-selftest)
endif()

if(BUILD_TESTING)
 find_package(Python3 REQUIRED COMPONENTS Interpreter)
 add_test(NAME ethercat-runtime-cli COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/src/ethercat_runtime_cli_selftest.py" "$<TARGET_FILE:ecmotion-rt>" "${CMAKE_CURRENT_SOURCE_DIR}/src/ethercat_runtime_config_fixture.json" "${CMAKE_CURRENT_SOURCE_DIR}/../../config/is620n-machine.json")
endif()

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

if(BUILD_TESTING)
 add_executable(ecmotion-motion-correction-selftest src/motion_correction_selftest.cpp)
 target_link_libraries(ecmotion-motion-correction-selftest PRIVATE ecmotion-axis-control ecmotion-io-cycle)
 target_compile_options(ecmotion-motion-correction-selftest PRIVATE -Wall -Wextra -Wpedantic -Werror)
 add_test(NAME motion-correction COMMAND ecmotion-motion-correction-selftest "${CMAKE_CURRENT_SOURCE_DIR}/../../config/is620n-machine.json")
endif()
