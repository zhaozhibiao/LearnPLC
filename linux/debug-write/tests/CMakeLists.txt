# Native replacement for the former interpreter-based fixture runners.
set(fixture "${CMAKE_CURRENT_BINARY_DIR}/handler-fixture")
file(MAKE_DIRECTORY "${fixture}/utils")
configure_file(../src/debug_handler.c "${fixture}/debug_handler.c" COPYONLY)
file(WRITE "${fixture}/debug_handler.h" "#include <stdbool.h>\n#include <stdint.h>\n#include <stddef.h>\nsize_t process_debug_data(uint8_t*,size_t);\nvoid debug_write_program_begin_load(void);\nvoid debug_write_program_end_load(bool);\nvoid debug_write_program_invalidate(void);\n")
file(WRITE "${fixture}/debug_write_journal.h" "#include <stdint.h>\nenum {DBGW_OP_WRITE=0,DBGW_OP_FORCE=1,DBGW_OP_UNFORCE=2};\nint runtime_external_write(uint8_t,uint16_t,uint8_t,const uint8_t*,uint16_t);\nvoid debug_write_journal_reset(void);\n")
file(WRITE "${fixture}/plc_state_manager.h" "enum {PLC_STATE_RUNNING=1};\nint plc_get_state(void);\n")
file(WRITE "${fixture}/image_tables.h" [=[
#include <stdbool.h>
#include <stdint.h>
extern uint8_t (*ext_strucpp_debug_array_count)(void);
extern uint16_t (*ext_strucpp_debug_elem_count)(uint8_t);
extern uint16_t (*ext_strucpp_debug_size)(uint8_t,uint16_t);
extern uint8_t (*ext_strucpp_debug_set)(uint8_t,uint16_t,bool,const uint8_t*,uint16_t);
extern uint16_t (*ext_strucpp_debug_read)(uint8_t,uint16_t,uint8_t*);
extern uint8_t (*ext_strucpp_debug_write)(uint8_t,uint16_t,const uint8_t*,uint16_t);
extern const char *ext_strucpp_program_md5;
]=])
file(WRITE "${fixture}/utils/log.h" "#define log_error(...) ((void)0)\n#define log_debug(...) ((void)0)\n")
file(WRITE "${fixture}/utils/utils.h" "extern unsigned long scan_counter;\n")
add_executable(debug-handler-test "${fixture}/debug_handler.c" handler_test.c)
target_include_directories(debug-handler-test PRIVATE "${fixture}")
target_link_libraries(debug-handler-test PRIVATE Threads::Threads)
target_compile_options(debug-handler-test PRIVATE -UNDEBUG -Wall -Wextra -Werror)
set_target_properties(debug-handler-test PROPERTIES C_STANDARD 11 C_STANDARD_REQUIRED YES)
add_test(NAME native-debug-handler COMMAND debug-handler-test)

add_executable(task-scheduling-test task_scheduling_test.cpp)
target_compile_options(task-scheduling-test PRIVATE -UNDEBUG -Wall -Wextra -Werror)
add_test(NAME native-task-scheduling COMMAND task-scheduling-test)

file(READ ../src/plc_state_manager.cpp manager)
string(FIND "${manager}" "    size_t spawned = 0;" first)
string(FIND "${manager}" "    /* Completion-signal condvar" last)
if(first LESS 0 OR last LESS first)
  message(FATAL_ERROR "Reviewed PLC startup transaction markers changed")
endif()
math(EXPR length "${last} - ${first}")
string(SUBSTRING "${manager}" ${first} ${length} transaction)
file(READ startup_prelude.cpp.in prelude)
file(READ startup_cases.cpp.in cases)
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/startup_test.cpp" "${prelude}${transaction}${cases}")
add_executable(task-startup-test "${CMAKE_CURRENT_BINARY_DIR}/startup_test.cpp")
target_include_directories(task-startup-test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/../src")
target_compile_options(task-startup-test PRIVATE -UNDEBUG -Wall -Wextra -Werror)
add_test(NAME native-task-startup COMMAND task-startup-test)
