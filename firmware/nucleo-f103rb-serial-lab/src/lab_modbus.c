#include "lab_modbus.h"
#include <stdbool.h>
#include <string.h>

#define REGISTER_BASE 0x1000u
#define REGISTER_COUNT 128u
#define COIL_BASE 0x1000u
#define COIL_COUNT 2048u

static uint16_t holding[REGISTER_COUNT];
static uint8_t coils[COIL_COUNT / 8u];
static uint32_t reset_at, current_time, write_commits, successful_reads;
static bool current_button, led_dirty;

void lab_modbus_reset(uint32_t now) {
    static const uint16_t initial[] = {0,0,0,0,0xff85,0x00fa,0x1234,0x5678,0x41c8,0x0000};
    memset(holding,0,sizeof(holding));
    memcpy(holding,initial,sizeof(initial));
    memset(coils,0,sizeof(coils));
    reset_at = current_time = now;
    write_commits = successful_reads = 0;
    current_button = false;
    led_dirty = true;
}

void lab_modbus_update(uint32_t now, bool button) {
    current_time = now;
    current_button = button;
}

bool lab_modbus_led(void) { return (coils[0] & 1u) != 0; }
uint32_t lab_modbus_write_commits(void) { return write_commits; }
uint32_t lab_modbus_reads(void) { return successful_reads; }

bool lab_modbus_take_led(bool *enabled) {
    if (!enabled || !led_dirty) return false;
    *enabled = lab_modbus_led();
    led_dirty = false;
    return true;
}

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

static uint16_t get16(const uint8_t *bytes) {
    return (uint16_t)((uint16_t)bytes[0] << 8 | bytes[1]);
}

static void put16(uint8_t *bytes, uint16_t value) {
    bytes[0] = (uint8_t)(value >> 8);
    bytes[1] = (uint8_t)value;
}

static bool in_range(uint16_t address, uint16_t quantity, uint32_t base, uint32_t count) {
    return quantity != 0 && address >= base && (uint32_t)address + quantity <= base + count;
}

static size_t exception_reply(uint8_t function, uint8_t code, uint8_t *reply, size_t capacity) {
    if (capacity < 5u) return 0;
    reply[0] = 1; reply[1] = (uint8_t)(function | 0x80u); reply[2] = code;
    return finish(reply,3);
}

static size_t acknowledgement(uint8_t function, uint16_t address, uint16_t value, uint8_t *reply) {
    reply[0] = 1; reply[1] = function;
    put16(reply + 2,address);
    put16(reply + 4,value);
    return finish(reply,6);
}

static bool coil_value(size_t index) { return ((coils[index / 8u] >> (index % 8u)) & 1u) != 0; }

static void set_coil(size_t index, bool enabled) {
    uint8_t mask = (uint8_t)(1u << (index % 8u));
    if (enabled) coils[index / 8u] |= mask;
    else coils[index / 8u] &= (uint8_t)~mask;
}

static void input_snapshot(uint16_t *words) {
    uint32_t elapsed = current_time - reset_at;
    uint32_t writes = write_commits, reads = successful_reads;
    words[0] = (uint16_t)(elapsed >> 16); words[1] = (uint16_t)elapsed;
    words[2] = (uint16_t)(writes >> 16); words[3] = (uint16_t)writes;
    words[4] = (uint16_t)(reads >> 16); words[5] = (uint16_t)reads;
    words[6] = (uint16_t)(current_button ? 1u : 0u); words[7] = (uint16_t)(lab_modbus_led() ? 1u : 0u);
    words[8] = 0xff85; words[9] = 250;
    words[10] = 0x1234; words[11] = 0x5678;
    words[12] = 0x41c8; words[13] = 0;
}

static size_t read_reply(const uint8_t *request, size_t length, uint8_t function, uint8_t *reply, size_t capacity) {
    if (length != 8u) return exception_reply(function,3,reply,capacity);
    uint16_t address = get16(request + 2), quantity = get16(request + 4);
    bool bits = function <= 2u;
    if (!quantity || quantity > (bits ? 2000u : 125u)) return exception_reply(function,3,reply,capacity);
    bool legacy = !bits && address < 2u && quantity <= 2u - address;
    bool corrupt = !bits && address == 0x0100u && quantity == 2u;
    if (!(legacy || corrupt || in_range(address,quantity,bits ? COIL_BASE : REGISTER_BASE,bits ? COIL_COUNT : REGISTER_COUNT)))
        return exception_reply(function,2,reply,capacity);
    size_t byte_count = bits ? ((size_t)quantity + 7u) / 8u : (size_t)quantity * 2u;
    if (capacity < byte_count + 5u) return 0;

    // The deliberate legacy CRC-fault endpoint is not a successful read.
    if (!corrupt) ++successful_reads;
    uint16_t snapshot[14] = {0};
    if (function == 4u && !legacy && !corrupt) input_snapshot(snapshot);
    reply[0] = 1; reply[1] = function; reply[2] = (uint8_t)byte_count;
    if (bits) {
        size_t first = (size_t)address - COIL_BASE;
        memset(reply + 3,0,byte_count);
        for (size_t i = 0; i < quantity; ++i) {
            size_t index = first + i;
            bool enabled = function == 1u ? coil_value(index)
                : index == 0u ? current_button : index == 1u && lab_modbus_led();
            if (enabled) reply[3u + i / 8u] |= (uint8_t)(1u << (i % 8u));
        }
    } else {
        static const uint16_t old_words[] = {250,100};
        for (size_t i = 0; i < quantity; ++i) {
            uint16_t value = 0;
            if (legacy || corrupt) value = old_words[(corrupt ? 0u : (size_t)address) + i];
            else {
                size_t index = (size_t)address - REGISTER_BASE + i;
                if (function == 3u) value = holding[index];
                else if (index < 14u) value = snapshot[index];
            }
            put16(reply + 3u + i * 2u,value);
        }
    }
    size_t result = finish(reply,byte_count + 3u);
    if (corrupt) reply[result - 1u] ^= 1u;
    return result;
}

static size_t single_write_reply(const uint8_t *request, size_t length, uint8_t function, uint8_t *reply, size_t capacity) {
    if (length != 8u) return exception_reply(function,3,reply,capacity);
    uint16_t address = get16(request + 2), value = get16(request + 4);
    bool bit = function == 5u;
    if (bit && value != 0u && value != 0xff00u) return exception_reply(function,3,reply,capacity);
    if (!in_range(address,1,bit ? COIL_BASE : REGISTER_BASE,bit ? COIL_COUNT : REGISTER_COUNT))
        return exception_reply(function,2,reply,capacity);
    if (capacity < 8u) return 0;

    if (bit) {
        size_t index = (size_t)address - COIL_BASE;
        set_coil(index,value != 0u);
        if (index == 0u) led_dirty = true;
    } else holding[(size_t)address - REGISTER_BASE] = value;
    ++write_commits;
    return acknowledgement(function,address,value,reply);
}

static size_t multiple_write_reply(const uint8_t *request, size_t length, uint8_t function, uint8_t *reply, size_t capacity) {
    if (length < 9u) return exception_reply(function,3,reply,capacity);
    uint16_t address = get16(request + 2), quantity = get16(request + 4);
    bool bits = function == 15u;
    if (!quantity || quantity > (bits ? 1968u : 123u)) return exception_reply(function,3,reply,capacity);
    size_t byte_count = bits ? ((size_t)quantity + 7u) / 8u : (size_t)quantity * 2u;
    if (request[6] != byte_count || length != byte_count + 9u)
        return exception_reply(function,3,reply,capacity);
    if (bits && quantity % 8u && (request[6u + byte_count] & (uint8_t)(0xffu << (quantity % 8u))))
        return exception_reply(function,3,reply,capacity);
    if (!in_range(address,quantity,bits ? COIL_BASE : REGISTER_BASE,bits ? COIL_COUNT : REGISTER_COUNT))
        return exception_reply(function,2,reply,capacity);
    if (capacity < 8u) return 0;

    size_t first = (size_t)address - (bits ? COIL_BASE : REGISTER_BASE);
    for (size_t i = 0; i < quantity; ++i) {
        if (bits) set_coil(first + i,((request[7u + i / 8u] >> (i % 8u)) & 1u) != 0);
        else holding[first + i] = get16(request + 7u + i * 2u);
    }
    if (bits && first == 0u) led_dirty = true;
    ++write_commits;
    return acknowledgement(function,address,quantity,reply);
}

size_t lab_modbus_reply(const uint8_t *request, size_t length, uint8_t *reply, size_t capacity) {
    if (!request || !reply || length < 4u || length > LAB_MODBUS_MAX_BYTES || request[0] != 1u ||
        lab_modbus_crc(request,length) != 0 || (request[1] & 0x80u)) return 0;
    uint8_t function = request[1];
    if (function >= 1u && function <= 4u) return read_reply(request,length,function,reply,capacity);
    if (function == 5u || function == 6u) return single_write_reply(request,length,function,reply,capacity);
    if (function == 15u || function == 16u) return multiple_write_reply(request,length,function,reply,capacity);
    return exception_reply(function,1,reply,capacity);
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
