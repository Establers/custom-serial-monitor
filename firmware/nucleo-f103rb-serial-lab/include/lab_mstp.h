#ifndef SERIAL_LAB_MSTP_H
#define SERIAL_LAB_MSTP_H
#include <stddef.h>
#include <stdint.h>
#define LAB_MSTP_MAX_DATA 501u
#define LAB_MSTP_MAX_BYTES 512u
uint8_t lab_mstp_header_crc(const uint8_t *bytes, size_t length);
uint16_t lab_mstp_data_crc(const uint8_t *bytes, size_t length);
/* Isolated UART bench, MAC 1: Test Request -> Test Response, echo payload.
 * Exact payload CRCH/CRCD injects a bad response header/data CRC for analysis.
 * No token manager, discovery, application services, or RS-485 driver is implied. */
size_t lab_mstp_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity);
#endif
