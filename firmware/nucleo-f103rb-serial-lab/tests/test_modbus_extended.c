#include "lab_modbus.h"
#include <assert.h>
#include <string.h>

static unsigned extended_checks;
#define CHECK(value) do { assert(value); ++extended_checks; } while (0)

static uint16_t word(const uint8_t *bytes) { return (uint16_t)((uint16_t)bytes[0] << 8 | bytes[1]); }
static void put(uint8_t *bytes, uint16_t value) { bytes[0] = (uint8_t)(value >> 8); bytes[1] = (uint8_t)value; }

static size_t seal(uint8_t *bytes, size_t body) {
    uint16_t crc = lab_modbus_crc(bytes,body);
    bytes[body] = (uint8_t)crc; bytes[body + 1u] = (uint8_t)(crc >> 8);
    return body + 2u;
}

static size_t request(uint8_t *bytes, uint8_t function, uint16_t address, uint16_t value) {
    bytes[0] = 1; bytes[1] = function;
    put(bytes + 2,address); put(bytes + 4,value);
    return seal(bytes,6);
}

static size_t multiple(uint8_t *bytes, uint8_t function, uint16_t address, uint16_t quantity) {
    bool bits = function == 15u;
    size_t count = bits ? ((size_t)quantity + 7u) / 8u : (size_t)quantity * 2u;
    bytes[0] = 1; bytes[1] = function;
    put(bytes + 2,address); put(bytes + 4,quantity); bytes[6] = (uint8_t)count;
    memset(bytes + 7,0,count);
    for (size_t i = 0; i < quantity; ++i) {
        if (bits) { if (i % 3u == 0u) bytes[7u + i / 8u] |= (uint8_t)(1u << (i % 8u)); }
        else put(bytes + 7u + i * 2u,(uint16_t)(0x3000u + i));
    }
    return seal(bytes,count + 7u);
}

static void exception(const uint8_t *bytes, size_t length, uint8_t function, uint8_t code) {
    CHECK(length == 5u);
    CHECK(bytes[0] == 1u && bytes[1] == (uint8_t)(function | 0x80u) && bytes[2] == code);
    CHECK(lab_modbus_crc(bytes,length) == 0u);
}

static void read_words(uint8_t function, uint16_t address, uint16_t quantity, uint8_t *reply) {
    uint8_t bytes[8]; request(bytes,function,address,quantity);
    size_t count = lab_modbus_reply(bytes,sizeof(bytes),reply,LAB_MODBUS_MAX_BYTES);
    CHECK(count == 5u + (size_t)quantity * 2u);
    CHECK(reply[0] == 1u && reply[1] == function && reply[2] == quantity * 2u);
    CHECK(lab_modbus_crc(reply,count) == 0u);
}

static void consume_led(bool expected) {
    bool enabled = !expected;
    CHECK(lab_modbus_take_led(&enabled));
    CHECK(enabled == expected);
    CHECK(!lab_modbus_take_led(&enabled));
}

static void legacy_and_initial_values(void) {
    uint8_t bytes[8], reply[LAB_MODBUS_MAX_BYTES];
    const uint16_t initial[] = {0,0,0,0,0xff85,250,0x1234,0x5678,0x41c8,0};
    lab_modbus_reset(100);
    CHECK(!lab_modbus_take_led(NULL));
    consume_led(false);
    for (uint8_t function = 3; function <= 4; ++function) {
        read_words(function,0,2,reply);
        CHECK(word(reply + 3) == 250u && word(reply + 5) == 100u);
        uint32_t before = lab_modbus_reads();
        request(bytes,function,0x0100,2);
        CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 9u);
        CHECK(word(reply + 3) == 250u && word(reply + 5) == 100u && lab_modbus_crc(reply,9) != 0u);
        CHECK(lab_modbus_reads() == before);
        const uint16_t invalid[] = {2,0x0200,0xffff,1};
        for (size_t i = 0; i < sizeof(invalid) / sizeof(invalid[0]); ++i) {
            request(bytes,function,invalid[i],2);
            exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),function,2);
        }
    }
    read_words(3,0x1000,125,reply);
    for (size_t i = 0; i < 125u; ++i) CHECK(word(reply + 3u + i * 2u) == (i < 10u ? initial[i] : 0u));
    read_words(3,0x107d,3,reply);
    CHECK(word(reply + 3) == 0u && word(reply + 7) == 0u);
    request(bytes,3,0x107f,2);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),3,2);
    request(bytes,3,0x1000,126);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),3,3);
    request(bytes,6,0,123);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),6,2);
    CHECK(lab_modbus_write_commits() == 0u);
}

static void holding_writes_and_readonly_inputs(void) {
    uint8_t bytes[LAB_MODBUS_MAX_BYTES], reply[LAB_MODBUS_MAX_BYTES];
    lab_modbus_reset(100);
    consume_led(false);
    lab_modbus_update(100u + 0x12345678u,true);
    request(bytes,6,0x107f,0xabcd);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u && !memcmp(bytes,reply,8));
    CHECK(lab_modbus_write_commits() == 1u);
    read_words(3,0x107f,1,reply); CHECK(word(reply + 3) == 0xabcdu);
    size_t length = multiple(bytes,16,0x1000,123);
    CHECK(length == 255u);
    CHECK(lab_modbus_reply(bytes,length,reply,sizeof(reply)) == 8u);
    CHECK(!memcmp(bytes,reply,6) && lab_modbus_crc(reply,8) == 0u);
    CHECK(lab_modbus_write_commits() == 2u);
    read_words(3,0x1000,125,reply);
    for (size_t i = 0; i < 123u; ++i) CHECK(word(reply + 3u + i * 2u) == 0x3000u + i);
    CHECK(word(reply + 249) == 0u && word(reply + 251) == 0u);
    read_words(4,0x1000,14,reply);
    CHECK(word(reply + 3) == 0x1234u && word(reply + 5) == 0x5678u);
    CHECK(word(reply + 7) == 0u && word(reply + 9) == 2u);
    CHECK(word(reply + 11) == 0u && word(reply + 13) == lab_modbus_reads());
    CHECK(word(reply + 15) == 1u && word(reply + 17) == 0u);
    const uint16_t expected[] = {0xff85,250,0x1234,0x5678,0x41c8,0};
    for (size_t i = 0; i < 6u; ++i) CHECK(word(reply + 19u + i * 2u) == expected[i]);
    read_words(4,0x107f,1,reply); CHECK(word(reply + 3) == 0u);
    bool enabled;
    CHECK(!lab_modbus_take_led(&enabled));
}

static void coils_and_discrete_inputs(void) {
    uint8_t bytes[LAB_MODBUS_MAX_BYTES], reply[LAB_MODBUS_MAX_BYTES];
    lab_modbus_reset(0); consume_led(false);
    request(bytes,5,0x1000,0xff00);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u && !memcmp(bytes,reply,8));
    CHECK(lab_modbus_led()); consume_led(true);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u);
    CHECK(lab_modbus_write_commits() == 2u); consume_led(true);
    request(bytes,5,0x17ff,0xff00);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u);
    bool enabled; CHECK(!lab_modbus_take_led(&enabled));
    request(bytes,1,0x17ff,1);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 6u && reply[2] == 1u && reply[3] == 1u);
    size_t length = multiple(bytes,15,0x1000,1968);
    CHECK(length == 255u && lab_modbus_reply(bytes,length,reply,sizeof(reply)) == 8u);
    CHECK(lab_modbus_write_commits() == 4u && lab_modbus_crc(reply,8) == 0u);
    consume_led(true);
    request(bytes,1,0x1000,2000);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 255u && reply[2] == 250u);
    for (size_t i = 0; i < 2000u; ++i) CHECK(((reply[3u + i / 8u] >> (i % 8u)) & 1u) == (i < 1968u && i % 3u == 0u ? 1u : 0u));
    CHECK(lab_modbus_crc(reply,255) == 0u);
    request(bytes,1,0x1000,9);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 7u && reply[3] == 0x49u && reply[4] == 0u);
    lab_modbus_update(50,true);
    request(bytes,2,0x1000,2000);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 255u && reply[3] == 3u);
    for (size_t i = 4; i < 253u; ++i) CHECK(reply[i] == 0u);
    request(bytes,1,0x17ff,2);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),1,2);
    request(bytes,1,0x1000,2001);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),1,3);
    request(bytes,2,0x0fff,1);
    exception(reply,lab_modbus_reply(bytes,8,reply,sizeof(reply)),2,2);
    request(bytes,5,0x1000,0);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u && !lab_modbus_led()); consume_led(false);
    request(bytes,2,0x1000,2);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 6u && reply[3] == 1u);
}

static void malformed_writes_have_no_partial_effects(void) {
    uint8_t bytes[LAB_MODBUS_MAX_BYTES + 1u], reply[LAB_MODBUS_MAX_BYTES];
    for (unsigned case_index = 0; case_index < 14u; ++case_index) {
        lab_modbus_reset(0); consume_led(false);
        size_t length = multiple(bytes,16,0x1000,2);
        uint8_t code = 3, function = 16;
        bool silent = false;
        switch (case_index) {
            case 0: bytes[6] = 2; break;
            case 1: put(bytes + 2,0x107f); code = 2; break;
            case 2: put(bytes + 4,0); break;
            case 3: put(bytes + 4,124); break;
            case 4: --length; break;
            case 5: bytes[length] = 0; ++length; break;
            case 6: bytes[0] = 0; silent = true; break;
            case 7: bytes[0] = 2; silent = true; break;
            case 8: bytes[1] = 0x90; silent = true; break;
            case 9: function = 5; length = request(bytes,5,0x1000,1); break;
            case 10: function = 15; length = multiple(bytes,15,0x1000,9); bytes[8] = 0x81; break;
            case 11: function = 15; length = multiple(bytes,15,0x17ff,2); code = 2; break;
            case 12: function = 7; bytes[1] = function; code = 1; break;
            default: silent = true; break;
        }
        seal(bytes,length - 2u);
        if (case_index == 13u) bytes[length - 1u] ^= 1u;
        size_t result = lab_modbus_reply(bytes,length,reply,sizeof(reply));
        if (silent) CHECK(result == 0u);
        else exception(reply,result,function,code);
        CHECK(lab_modbus_write_commits() == 0u && lab_modbus_reads() == 0u && !lab_modbus_led());
        bool enabled; CHECK(!lab_modbus_take_led(&enabled));
        read_words(3,0x1000,2,reply);
        CHECK(word(reply + 3) == 0u && word(reply + 5) == 0u);
    }
}

static void capacity_and_reset_boundaries(void) {
    uint8_t bytes[LAB_MODBUS_MAX_BYTES], reply[LAB_MODBUS_MAX_BYTES];
    lab_modbus_reset(0); consume_led(false);
    size_t length = multiple(bytes,16,0x1000,2);
    for (size_t capacity = 0; capacity < 8u; ++capacity) {
        memset(reply,0xa5,sizeof(reply));
        CHECK(lab_modbus_reply(bytes,length,reply,capacity) == 0u);
        CHECK(lab_modbus_write_commits() == 0u);
        for (size_t i = 0; i < sizeof(reply); ++i) CHECK(reply[i] == 0xa5u);
    }
    request(bytes,5,0x1000,0xff00);
    CHECK(lab_modbus_reply(bytes,8,reply,7) == 0u && !lab_modbus_led());
    CHECK(lab_modbus_write_commits() == 0u);
    CHECK(lab_modbus_reply(bytes,8,NULL,sizeof(reply)) == 0u);
    CHECK(lab_modbus_reply(NULL,8,reply,sizeof(reply)) == 0u);
    request(bytes,3,0,2);
    CHECK(lab_modbus_reply(bytes,8,reply,8) == 0u && lab_modbus_reads() == 0u);
    CHECK(lab_modbus_reply(bytes,8,reply,9) == 9u && lab_modbus_reads() == 1u);
    request(bytes,3,0x1000,0);
    CHECK(lab_modbus_reply(bytes,8,reply,4) == 0u);
    request(bytes,6,0x1000,0xffff);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u);
    request(bytes,5,0x1000,0xff00);
    CHECK(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u);
    lab_modbus_reset(0xfffffff0u); lab_modbus_update(0x10u,false);
    CHECK(lab_modbus_write_commits() == 0u && lab_modbus_reads() == 0u && !lab_modbus_led());
    consume_led(false);
    read_words(4,0x1000,14,reply);
    CHECK(word(reply + 3) == 0u && word(reply + 5) == 32u);
    CHECK(word(reply + 7) == 0u && word(reply + 9) == 0u);
    CHECK(word(reply + 11) == 0u && word(reply + 13) == 1u);
    CHECK(word(reply + 15) == 0u && word(reply + 17) == 0u);
    read_words(3,0x1000,10,reply); CHECK(word(reply + 3) == 0u && word(reply + 11) == 0xff85u);
}

static void counters_snapshot_the_current_read_across_word_carry(void) {
    uint8_t bytes[8], reply[LAB_MODBUS_MAX_BYTES];
    lab_modbus_reset(0);
    request(bytes,3,0,1);
    for (uint32_t i = 0; i < 65535u; ++i) assert(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 7u);
    CHECK(lab_modbus_reads() == 65535u);
    read_words(4,0x1004,2,reply);
    CHECK(word(reply + 3) == 1u && word(reply + 5) == 0u && lab_modbus_reads() == 65536u);
    request(bytes,6,0x1000,0);
    for (uint32_t i = 0; i < 65536u; ++i) assert(lab_modbus_reply(bytes,8,reply,sizeof(reply)) == 8u);
    CHECK(lab_modbus_write_commits() == 65536u);
    read_words(4,0x1002,4,reply);
    CHECK(word(reply + 3) == 1u && word(reply + 5) == 0u);
    CHECK(word(reply + 7) == 1u && word(reply + 9) == 1u && lab_modbus_reads() == 65537u);
}

unsigned lab_modbus_extended_tests(void) {
    extended_checks = 0;
    legacy_and_initial_values();
    holding_writes_and_readonly_inputs();
    coils_and_discrete_inputs();
    malformed_writes_have_no_partial_effects();
    capacity_and_reset_boundaries();
    counters_snapshot_the_current_read_across_word_carry();
    return extended_checks;
}
