// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Autonomy®

#ifndef DEBUG_HANDLER_H
#define DEBUG_HANDLER_H

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
size_t process_debug_data(uint8_t *data, size_t length);
/* Narrow FC46 lifecycle gate. Begin/end bracket symbols_init. Invalidate
 * before joining/stopping/unloading a program. Never hold across a join.
 * Gate -> debug journal is the only nested lock order. */
void debug_write_program_begin_load(void);
void debug_write_program_end_load(bool ready);
void debug_write_program_invalidate(void);
#ifdef __cplusplus
}
#endif

#endif // DEBUG_HANDLER_H
