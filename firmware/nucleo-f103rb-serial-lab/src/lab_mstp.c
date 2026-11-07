#include "lab_mstp.h"
#include <string.h>

uint8_t lab_mstp_header_crc(const uint8_t *bytes, size_t length) {
    uint8_t crc = 0xff;
    for (size_t i = 0; i < length; ++i) {
        crc ^= bytes[i];
        for (unsigned bit = 0; bit < 8; ++bit) crc = (uint8_t)((crc >> 1) ^ ((crc & 1) ? 0x81 : 0));
    }
    return (uint8_t)~crc;
}
uint16_t lab_mstp_data_crc(const uint8_t *bytes, size_t length) {
    uint16_t crc = 0xffff;
    for (size_t i = 0; i < length; ++i) {
        crc ^= bytes[i];
        for (unsigned bit = 0; bit < 8; ++bit) crc = (uint16_t)((crc >> 1) ^ ((crc & 1) ? 0x8408 : 0));
    }
    return (uint16_t)~crc;
}
size_t lab_mstp_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity) {
    if (!request || !reply || length < 8 || length > LAB_MSTP_MAX_BYTES || request[0] != 0x55 || request[1] != 0xff ||
        request[2] != 3 || request[3] != 1 || request[4] == 0xff || lab_mstp_header_crc(request + 2,5) != request[7]) return 0;
    size_t size = (size_t)request[5] << 8 | request[6];
    size_t wire_length = 8 + size + (size ? 2u : 0u);
    if (size > LAB_MSTP_MAX_DATA || capacity < wire_length ||
        (length != wire_length && !(length == wire_length + 1 && request[length - 1] == 0xff))) return 0;
    if (size && lab_mstp_data_crc(request + 8,size) != ((uint16_t)request[8 + size] | (uint16_t)request[9 + size] << 8)) return 0;
    memcpy(reply,request,wire_length); reply[2] = 4; reply[3] = request[4]; reply[4] = 1;
    reply[7] = lab_mstp_header_crc(reply + 2,5);
    if (size == 4 && !memcmp(request + 8,"CRCH",4)) reply[7] ^= 1;
    if (size == 4 && !memcmp(request + 8,"CRCD",4)) reply[wire_length - 1] ^= 1;
    return wire_length;
}
