#ifndef SERIAL_LAB_FIXTURES_H
#define SERIAL_LAB_FIXTURES_H
#include <stddef.h>
#include <stdint.h>

#define LAB_FIRMWARE_VERSION "1.2.0"
#define LAB_FIXTURE_NAMES "TEXT,UTF8,CP949,BYTES,NEWLINES,EVENTS,JSON,ANSI,CONTROL"
#define LAB_FIXTURE_COUNT 9u
/* No allocation. Returns zero for an unknown case or insufficient destination. */
size_t lab_fixture_copy(const char *name, uint8_t *destination, size_t capacity);
#endif
