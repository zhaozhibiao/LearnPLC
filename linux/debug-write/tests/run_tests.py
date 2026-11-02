"""Compile the real handler in an isolated directory with boundary stubs only."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

p = argparse.ArgumentParser()
p.add_argument("handler", type=Path)
p.add_argument("--cc", default=os.environ.get("CC", "cc"))
a = p.parse_args()
here = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(prefix="openplc-debug-write-") as td:
    d = Path(td)
    (d / "utils").mkdir()
    (d / "debug_handler.c").write_bytes(a.handler.read_bytes())
    (d / "debug_handler.h").write_text("#include <stdbool.h>\n#include <stdint.h>\n#include <stddef.h>\nsize_t process_debug_data(uint8_t*,size_t);\nvoid debug_write_program_begin_load(void);\nvoid debug_write_program_end_load(bool);\nvoid debug_write_program_invalidate(void);\n")
    (d / "debug_write_journal.h").write_text("#include <stdint.h>\nenum {DBGW_OP_WRITE=0,DBGW_OP_FORCE=1,DBGW_OP_UNFORCE=2};\nint runtime_external_write(uint8_t,uint16_t,uint8_t,const uint8_t*,uint16_t);\nvoid debug_write_journal_reset(void);\n")
    (d / "plc_state_manager.h").write_text("enum {PLC_STATE_RUNNING=1};\nint plc_get_state(void);\n")
    (d / "image_tables.h").write_text('''#include <stdbool.h>
#include <stdint.h>
extern uint8_t (*ext_strucpp_debug_array_count)(void);
extern uint16_t (*ext_strucpp_debug_elem_count)(uint8_t);
extern uint16_t (*ext_strucpp_debug_size)(uint8_t,uint16_t);
extern uint8_t (*ext_strucpp_debug_set)(uint8_t,uint16_t,bool,const uint8_t*,uint16_t);
extern uint16_t (*ext_strucpp_debug_read)(uint8_t,uint16_t,uint8_t*);
extern uint8_t (*ext_strucpp_debug_write)(uint8_t,uint16_t,const uint8_t*,uint16_t);
extern const char *ext_strucpp_program_md5;
''')
    (d / "utils/log.h").write_text("#define log_error(...) ((void)0)\n#define log_debug(...) ((void)0)\n")
    (d / "utils/utils.h").write_text("extern unsigned long scan_counter;\n")
    exe = d / ("handler-tests.exe" if os.name == "nt" else "handler-tests")
    subprocess.run([a.cc, "-std=c11", "-Wall", "-Wextra", "-Werror", "-pthread", "-I", str(d), str(d / "debug_handler.c"), str(here / "handler_test.c"), "-o", str(exe)], check=True)
    subprocess.run([str(exe)], check=True)
