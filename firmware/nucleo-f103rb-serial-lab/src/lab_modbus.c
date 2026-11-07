#include "lab_modbus.h"
#include <stdbool.h>
#include <string.h>

uint16_t lab_modbus_crc(const uint8_t *bytes, size_t length) {
    uint16_t crc = 0xffff;
    for (size_t i = 0; i < length; ++i) {
        crc ^= bytes[i];
        for (unsigned bit = 0; bit < 8; ++bit)
            crc = (crc & 1) ? (uint16_t)((crc >> 1) ^ 0xa001) : (uint16_t)(crc >> 1);
    }
    return crc;
}

static size_t finish(uint8_t *bytes, size_t length) {
    uint16_t crc = lab_modbus_crc(bytes, length);
    bytes[length] = (uint8_t)crc;
    bytes[length + 1] = (uint8_t)(crc >> 8);
    return length + 2;
}

size_t lab_modbus_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity) {
    if (!request || !reply || length < 4 || length > LAB_MODBUS_MAX_BYTES || request[0] != 1 ||
        lab_modbus_crc(request,length) != 0 || (request[1] & 0x80)) return 0;
    uint8_t exception = 0;
    uint16_t address = 0, quantity = 0;
    bool corrupt = false;
    if (request[1] != 3 && request[1] != 4) exception = 1;
    else if (length != 8) exception = 3;
    else {
        address = (uint16_t)((uint16_t)request[2] << 8 | request[3]);
        quantity = (uint16_t)((uint16_t)request[4] << 8 | request[5]);
        if (!quantity || quantity > 125) exception = 3;
        else if (address == 0x0100 && quantity == 2) corrupt = true;
        else if (address >= 2 || quantity > 2 - address) exception = 2;
    }
    size_t required = exception ? 5u : 5u + (size_t)quantity * 2u;
    if (capacity < required) return 0;
    reply[0] = 1; reply[1] = request[1];
    if (exception) {
        reply[1] |= 0x80; reply[2] = exception;
        return finish(reply,3);
    }
    static const uint16_t registers[] = {250,100};
    if (corrupt) address = 0;
    reply[2] = (uint8_t)(quantity * 2);
    for (size_t i = 0; i < quantity; ++i) {
        uint16_t value = registers[address + i];
        reply[3 + i * 2] = (uint8_t)(value >> 8);
        reply[4 + i * 2] = (uint8_t)value;
    }
    size_t result = finish(reply,required - 2);
    if (corrupt) reply[result - 1] ^= 1;
    return result;
}

size_t lab_modbus_copy(const char *name, uint8_t *destination, size_t capacity) {
    static const uint8_t request[] = {1,3,0,0,0,2,0xc4,0x0b};
    static const uint8_t normal[] = {1,3,4,0,250,0,100};
    uint8_t bytes[LAB_MODBUS_MAX_BYTES];
    size_t length;
    if (!strcmp(name,"NORMAL") || !strcmp(name,"CRC") || !strcmp(name,"TRUNCATED") || !strcmp(name,"GAP")) {
        memcpy(bytes,normal,sizeof(normal));
        length = finish(bytes,sizeof(normal));
        if (!strcmp(name,"CRC")) bytes[length - 1] ^= 1;
        if (!strcmp(name,"TRUNCATED")) length = 3;
    } else if (!strcmp(name,"EXCEPTION")) {
        bytes[0] = 1; bytes[1] = 0x83; bytes[2] = 2;
        length = finish(bytes,3);
    } else if (!strcmp(name,"PREFIX")) {
        const uint8_t body[] = {1,3,4,0,0,1,0x85};
        memcpy(bytes,body,sizeof(body)); length = finish(bytes,sizeof(body));
    } else if (!strcmp(name,"NESTED")) {
        bytes[0] = 1; bytes[1] = 8; bytes[2] = bytes[3] = 0;
        memcpy(bytes + 4,request,sizeof(request)); length = finish(bytes,12);
    } else if (!strcmp(name,"CORRUPT")) {
        bytes[0] = 1; bytes[1] = 3; bytes[2] = 10;
        memcpy(bytes + 3,request,sizeof(request)); bytes[11] = 0x11; bytes[12] = 0x22;
        length = finish(bytes,13); bytes[length - 1] ^= 1;
    } else if (!strcmp(name,"PAIR")) {
        memcpy(bytes,request,sizeof(request)); memcpy(bytes + 8,normal,sizeof(normal));
        length = 8 + finish(bytes + 8,sizeof(normal));
    } else if (!strcmp(name,"MAX")) {
        bytes[0] = 1; bytes[1] = 3; bytes[2] = 250;
        for (size_t i = 0; i < 250; ++i) bytes[3 + i] = (uint8_t)i;
        length = finish(bytes,253);
    } else return 0;
    if (!destination || capacity < length) return 0;
    memcpy(destination,bytes,length);
    return length;
}
