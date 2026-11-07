#ifndef SERIAL_LAB_MODBUS_H
#define SERIAL_LAB_MODBUS_H
#include <stddef.h>
#include <stdint.h>

#define LAB_MODBUS_MAX_BYTES 256u
#define LAB_MODBUS_CASES "NORMAL,CRC,EXCEPTION,PREFIX,NESTED,CORRUPT,PAIR,TRUNCATED,GAP,MAX"

/* Deterministic analyzer stimuli, not a Modbus server or a timing-conformance test. */
size_t lab_modbus_copy(const char *name, uint8_t *destination, size_t capacity);
uint16_t lab_modbus_crc(const uint8_t *bytes, size_t length);
/* Unit 1 read-only bench slave. Invalid CRC/broadcast/other units stay silent.
 * Address 0x0100, quantity 2 deliberately corrupts the response CRC. */
size_t lab_modbus_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity);
#endif
