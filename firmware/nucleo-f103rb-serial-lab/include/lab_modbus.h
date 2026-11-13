#ifndef SERIAL_LAB_MODBUS_H
#define SERIAL_LAB_MODBUS_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define LAB_MODBUS_MAX_BYTES 256u
#define LAB_MODBUS_CASES "NORMAL,CRC,EXCEPTION,PREFIX,NESTED,CORRUPT,PAIR,TRUNCATED,GAP,MAX"

/* Deterministic analyzer stimuli, not a Modbus server or a timing-conformance test. */
size_t lab_modbus_copy(const char *name, uint8_t *destination, size_t capacity);
uint16_t lab_modbus_crc(const uint8_t *bytes, size_t length);
/* Main-loop-only state. Reset requests one LED-off application; each write
 * touching coil 0 requests one more application, even if its value is unchanged. */
void lab_modbus_reset(uint32_t now);
void lab_modbus_update(uint32_t now, bool button);
bool lab_modbus_take_led(bool *enabled);
bool lab_modbus_led(void);
uint32_t lab_modbus_write_commits(void);
uint32_t lab_modbus_reads(void);
/* Unit 1 bench server; invalid CRC/broadcast/other units have no effects.
 * FC03/04 legacy addresses 0/1 remain 250/100. Extended word bank is
 * 0x1000..0x107f; the coil/discrete bank is 0x1000..0x17ff.
 * Address 0x0100, quantity 2 deliberately corrupts its legacy response CRC.
 * Entire requests and reply capacity are checked before applying writes or
 * counting successful reads. Input snapshots include their current read;
 * deliberate legacy bad-CRC responses are excluded from that counter. */
size_t lab_modbus_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity);
#endif
