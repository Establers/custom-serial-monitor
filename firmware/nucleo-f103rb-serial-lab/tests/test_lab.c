#include "lab.h"
#include "lab_fixtures.h"
#include <assert.h>
#include <stdio.h>
#include <string.h>

static uint8_t out[65536];
static size_t used;
static uint32_t clock_ms, switched;
static bool blocked, data_blocked, baud_blocked, was_reset, led_state, button_state;
static unsigned checks;
static bool write_bytes(const uint8_t *bytes, size_t length) {
    if (blocked) return false;
    assert(used + length < sizeof(out));
    memcpy(out + used, bytes, length); used += length; out[used] = 0;
    return true;
}
static void set_led(bool on) { led_state = on; }
static bool write_data(const uint8_t *bytes, size_t length) {
    return !data_blocked && write_bytes(bytes, length);
}
static bool get_button(void) { return button_state; }
static bool set_baud(uint32_t rate) { if (baud_blocked) return false; switched = rate; return true; }
static bool set_reset(void) { if (baud_blocked) return false; was_reset = true; return true; }
static void stats(uint32_t *drops, uint32_t *errors) { *drops = 7; *errors = 3; }
static const lab_platform platform = {write_bytes,write_data,set_led,get_button,set_baud,set_reset,stats};
static void clear(void) { used = 0; out[0] = 0; }
static void init(uint32_t now) {
    blocked = data_blocked = baud_blocked = was_reset = led_state = false;
    switched = 0; clock_ms = now; button_state = true; clear(); lab_init(&platform, now);
    /* Legacy exact-response cases explicitly disable the new boot-on background output. */
    const char quiet[] = "MONITOR OFF\n";
    for (size_t i = 0; i < sizeof(quiet) - 1; ++i) lab_receive((uint8_t)quiet[i], now);
    clear();
}
static void feed_bytes(const void *data, size_t length) {
    const uint8_t *bytes = data;
    for (size_t i = 0; i < length; ++i) lab_receive(bytes[i], clock_ms);
}
static void send(const char *text) { feed_bytes(text, strlen(text)); }
static void contains(const char *text) { assert(strstr((char *)out, text) != NULL); ++checks; }
static void equals(const char *text) { assert(used == strlen(text)); assert(!memcmp(out, text, used)); ++checks; }
static void poll(uint32_t now, unsigned times) { clock_ms = now; for (unsigned i = 0; i < times; ++i) lab_poll(now); }
static size_t packet(uint8_t *p, uint8_t type, uint32_t seq, const uint8_t *payload, size_t len) {
    p[0] = 0xa5; p[1] = 0x5a; p[2] = type;
    for (unsigned i = 0; i < 4; ++i) p[3 + i] = (uint8_t)(seq >> (8 * i));
    p[7] = (uint8_t)len; p[8] = (uint8_t)(len >> 8);
    if (len) memcpy(p + 9, payload, len);
    uint16_t crc = lab_crc16(p + 2, len + 7);
    p[9 + len] = (uint8_t)crc; p[10 + len] = (uint8_t)(crc >> 8);
    return len + 11;
}

static void monitor_tests(void) {
    init(0); button_state = false; lab_init(&platform, 0); clear();
    poll(999, 1); equals(""); poll(1000, 1);
    contains("HEARTBEAT seq=1 uptime_ms=1000 user=RELEASED");
    clear(); poll(1000, 5); poll(1999, 1); equals("");
    poll(2000, 1); contains("HEARTBEAT seq=2 uptime_ms=2000");
    clear(); button_state = true; poll(2010, 1); poll(2029, 1); equals("");
    button_state = false; poll(2030, 1); button_state = true; poll(2035, 1);
    poll(2054, 1); equals(""); poll(2055, 1);
    equals("INFO USER PRESS count=1 uptime_ms=2055\r\n");
    clear(); poll(2500, 10); equals("");
    button_state = false; poll(2600, 1); poll(2620, 1);
    equals("INFO USER RELEASE count=1 held_ms=565 uptime_ms=2620\r\n");
    clear(); send("MONITOR OFF\n"); equals("OK MONITOR OFF\r\n");
    clear(); poll(5000, 2); equals("");
    send("MONITOR ON\n"); clear(); poll(5999, 1); equals("");
    poll(6000, 1); contains("HEARTBEAT seq=3");
    send("STOP\n"); clear(); poll(8000, 2); equals("");
    send("MONITOR?\n"); contains("MONITOR enabled=0");
    clear(); send("HELP\n"); contains("MONITOR ON|OFF");
    init(0xfffffff0u); button_state = false; lab_init(&platform, 0xfffffff0u); clear();
    poll(983, 1); equals(""); poll(984, 1); contains("HEARTBEAT seq=1");
    init(0xfffffff0u); button_state = false; lab_init(&platform, 0xfffffff0u); clear();
    button_state = true; poll(0xfffffff8u, 1); poll(11, 1); equals("");
    poll(12, 1); contains("USER PRESS count=1");
    init(0); send("MONITOR ON\n"); clear(); data_blocked = true; poll(1000, 1); equals("");
    send("PING reserved\n"); equals("PONG reserved\r\n");
    data_blocked = false; clear(); poll(2000, 1); contains("missed=1");
    clear(); poll(9000, 10); contains("HEARTBEAT seq=3");
    assert(!strstr((char *)out, "HEARTBEAT seq=4")); ++checks;
}

static void text_tests(void) {
    init(0); send("PING one\r\nPING two\nPING three\r");
    equals("PONG one\r\nPONG two\r\nPONG three\r\n");
    clear(); send("ECHO   spaces  \r\n"); equals("ECHO   spaces  \r\n");
    clear(); send("LED ON\nBUTTON?\nSTATUS?\n");
    assert(led_state); contains("BUTTON 1"); contains("rx_drops=7 uart_errors=3");
    clear(); send("UTF8\nANSI\nNEWLINES\nLEVELS\n");
    contains("\xf0\x9f\x9a\x80"); contains("\x1b[32mGREEN"); contains("CRLF\r\nLF\nCR\rEND\r\n"); contains("[ERROR] test error");
    clear(); send("SILENT\n"); equals("");
    clear(); send("RESET\n"); contains("ERR"); assert(!was_reset);
    clear(); send("LONG 2048\n"); poll(0, 30);
    assert(used == 2050); for (size_t i = 0; i < 2048; ++i) assert(out[i] == 'A' + i % 26); ++checks;
    init(0); send("DELAY 10 original\nLONG 2048\n"); contains("ERR BUSY");
    clear(); poll(10,10); equals("original\r\n");
}

static void scheduling_tests(void) {
    init(0); send("DELAY 200 delayed\n"); poll(199, 1); equals("");
    poll(200, 1); equals("delayed\r\n");
    clear(); send("SPLIT 50 ABC\n"); poll(250, 1); equals("A");
    poll(299, 1); equals("A"); poll(300, 1); equals("AB");
    send("STOP\n"); clear(); poll(1000, 5); equals("");
    init(0xfffffff0u); send("DELAY 30 WRAP\n"); poll(5, 1); equals(""); poll(14, 1); equals("WRAP\r\n");
    init(0); send("BAUD 230400\n"); contains("SWITCH_IN_MS=500");
    baud_blocked = true; poll(500, 1); assert(switched == 0);
    baud_blocked = false; poll(501, 1); assert(switched == 230400); contains("READY BAUD 230400");
    send("RESET CONFIRM\n"); poll(1000, 1); assert(!was_reset);
    baud_blocked = true; poll(1001, 1); assert(!was_reset);
    baud_blocked = false; poll(1002, 1); assert(was_reset); ++checks;
}

static void generator_tests(void) {
    init(0); send("STREAM START 10 3\n"); clear(); poll(10,1); poll(20,1); poll(30,1); poll(40,1);
    contains("seq=1 "); contains("seq=3 "); assert(!strstr((char *)out,"seq=4 "));
    init(0); send("FAULT DROP 3\nFAULT DUP 2\nBURST 6 8\n"); clear(); poll(0, 10);
    equals("BURST seq=1 payload=ABCDEFGH\r\nBURST seq=2 payload=ABCDEFGH\r\nBURST seq=2 payload=ABCDEFGH\r\nBURST seq=4 payload=ABCDEFGH\r\nBURST seq=4 payload=ABCDEFGH\r\nBURST seq=5 payload=ABCDEFGH\r\n");
    init(0); send("BURST 3\n"); clear(); blocked = true; poll(0,10); equals("");
    blocked = false; poll(0,3); contains("seq=1 "); contains("seq=3 ");
    init(0); send("BURST 100\n"); clear(); data_blocked = true; poll(0,100);
    send("PING control\nSTOP\n"); equals("PONG control\r\nOK STOP\r\n");
    clear(); data_blocked = false; poll(1,200); equals("");
    init(0); send("FAULT CRC 2\nHEX 2\n"); clear(); poll(0,2);
    assert(used == 86); assert(out[2] == 0x90 && out[3] == 1 && out[46] == 2);
    assert(lab_crc16(out + 2,39) == ((uint16_t)out[41] | (uint16_t)out[42] << 8));
    assert(lab_crc16(out + 45,39) != ((uint16_t)out[84] | (uint16_t)out[85] << 8)); ++checks;
}

static void binary_tests(void) {
    uint8_t p[267], payload[256];
    for (unsigned i = 0; i < sizeof(payload); ++i) payload[i] = (uint8_t)i;
    init(0); size_t len = packet(p,2,0xabcdef01,payload,sizeof(payload));
    feed_bytes(p,len); assert(used == len && out[2] == 0x82); assert(!memcmp(out+9,payload,256));
    assert(lab_crc16(out+2,263) == ((uint16_t)out[265] | (uint16_t)out[266] << 8)); ++checks;
    clear(); p[len-1] ^= 1; feed_bytes(p,len); assert(out[2] == 0xe0); assert(!memcmp(out+9,"CRC",3)); ++checks;
    clear(); uint8_t bad_length[] = {0xa5,0x5a,1,0,0,0,0,1,4}; feed_bytes(bad_length,sizeof(bad_length));
    assert(out[2] == 0xe0); assert(!memcmp(out+9,"LEN",3)); ++checks;
    clear(); send("PING recovery\n"); equals("PONG recovery\r\n");
    clear(); feed_bytes(p,5); clock_ms += 501; send("PING timeout\n"); equals("PONG timeout\r\n");
    clear(); len = packet(p,1,4,NULL,0); feed_bytes(p,len); feed_bytes(p,len); assert(used == len * 2); ++checks;
    clear(); uint8_t noise[] = {0xa5,0xa5}; feed_bytes(noise,2); feed_bytes(p+1,len-1); assert(used == len); ++checks;
}

static const size_t binary_lengths[] = {0,1,255,256,257,511,512,513,1023,1024};
static uint8_t boundary_value(size_t index) { return (uint8_t)(index * 73u + (index >> 8) * 19u + 0x5au); }

static size_t boundary_packet(uint8_t *p, size_t length) {
    uint8_t payload[LAB_MAX_PAYLOAD];
    for (size_t i = 0; i < length; ++i) payload[i] = boundary_value(i);
    return packet(p,2,0x34560000u + (uint32_t)length,payload,length);
}

static void assert_boundary(size_t length) {
    assert(used == length + LAB_FRAME_OVERHEAD && out[0] == 0xa5 && out[1] == 0x5a && out[2] == 0x82);
    assert(out[7] == (uint8_t)length && out[8] == (uint8_t)(length >> 8));
    uint32_t seq = 0;
    for (unsigned i = 0; i < 4; ++i) seq |= (uint32_t)out[3 + i] << (8u * i);
    assert(seq == 0x34560000u + (uint32_t)length);
    for (size_t i = 0; i < length; ++i) assert(out[9 + i] == boundary_value(i));
    uint16_t expected = (uint16_t)out[9 + length] | (uint16_t)out[10 + length] << 8;
    assert(lab_crc16(out + 2,length + 7) == expected); ++checks;
}

static void large_binary_tests(void) {
    uint8_t p[LAB_MAX_FRAME_BYTES];
    for (size_t case_index = 0; case_index < sizeof(binary_lengths)/sizeof(binary_lengths[0]); ++case_index) {
        size_t length = binary_lengths[case_index];
        size_t size = boundary_packet(p,length);
        init(0); feed_bytes(p,size); assert_boundary(length);
        /* Header, multi-byte LEN, payload and CRC may arrive in arbitrary chunks. */
        init(0);
        for (size_t i = 0; i + 1 < size; ++i) {
            if (i % 31 == 0) clock_ms += 3;
            lab_receive(p[i],clock_ms);
            assert(used == 0);
        }
        lab_receive(p[size - 1],clock_ms); assert_boundary(length);
    }
    init(0); size_t size = boundary_packet(p,1024);
    feed_bytes(p,size); feed_bytes(p,size);
    assert(used == size * 2 && !memcmp(out,out + size,size)); ++checks;
    init(0); p[size - 1] ^= 1; feed_bytes(p,size);
    assert(used == 14 && out[2] == 0xe0 && !memcmp(out + 9,"CRC",3)); ++checks;
    init(0);
    const uint8_t oversized[] = {0xa5,0x5a,2,1,0,0,0,1,4};
    feed_bytes(oversized,sizeof(oversized));
    assert(used == 14 && out[2] == 0xe0 && !memcmp(out + 9,"LEN",3)); ++checks;
    clear(); send("PING after-1025\n"); equals("PONG after-1025\r\n");
    init(0); size = boundary_packet(p,1024); feed_bytes(p,700);
    clock_ms += 501; send("PING after-partial-1024\n"); equals("PONG after-partial-1024\r\n");
}

static void malformed_tests(void) {
    init(0);
    const char *bad[] = {"BURST 42949672960\n","HEX 0\n","BAUD 12345\n","FAULT DROP 1\n",
        "STREAM START 1 0\n","LONG -1\n","LONG 2049\n","DELAY 1\n","LED MAYBE\n","PING\0X"};
    for (size_t i = 0; i + 1 < sizeof(bad)/sizeof(bad[0]); ++i) {
        clear(); send(bad[i]); contains("ERR");
    }
    clear(); for (unsigned i = 0; i < 2000; ++i) lab_receive('X',0); send("\nPING alive\n");
    contains("ERR LINE_TOO_LONG"); contains("PONG alive");
    clear(); const uint8_t nul[] = {'P','I','N','G',0,'\n'}; feed_bytes(nul,sizeof(nul)); contains("ERR");
    clear(); send("PING\n"); equals("PONG\r\n");
}

static const char *fixture_names[] = {"TEXT","UTF8","CP949","BYTES","NEWLINES","EVENTS","JSON","ANSI","CONTROL"};

static void fixture_tests(void) {
    init(0); send("BUILD?\n"); contains("BUILD version=" LAB_FIRMWARE_VERSION " protocol=1 id=");
    clear(); send("CAPS?\n"); contains("line_max=511 payload_max=1024 long_max=2048 fixture_count=9");
    contains("fixtures=" LAB_FIXTURE_NAMES);
    clear(); send("SELFTEST\n"); contains("SELFTEST PASS crc=PASS fixtures=PASS limits=PASS build=");
    assert(!was_reset && !led_state && switched == 0); ++checks;
    init(0); send("RUN BYTES unique_1\n"); poll(0,10);
    char *header_end = strstr((char *)out,"\r\n"); assert(header_end);
    size_t offset = (size_t)(header_end - (char *)out) + 2;
    assert(used > offset + 256);
    for (unsigned i = 0; i < 256; ++i) assert(out[offset + i] == (uint8_t)i);
    assert(!memcmp(out + offset + 256,"\r\nEND run=unique_1 case=BYTES\r\n",31)); ++checks;
    for (size_t i = 0; i < sizeof(fixture_names)/sizeof(fixture_names[0]); ++i) {
        uint8_t destination[256];
        size_t size = lab_fixture_copy(fixture_names[i],destination,sizeof(destination));
        assert(size > 0 && size <= 256); ++checks;
    }
    uint8_t destination[256];
    assert(!lab_fixture_copy("unknown",destination,sizeof(destination))); ++checks;
    assert(!lab_fixture_copy("BYTES",destination,255)); ++checks;
    init(0); send("RUN TEXT first\nRUN TEXT second\n"); contains("ERR BUSY");
    clear(); poll(0,10); contains("BEGIN run=first case=TEXT"); assert(!strstr((char *)out,"second"));
    init(0); send("FAULT DROP 2\n"); clear(); send("RUN TEXT exclusive\n"); contains("ERR BUSY");
    init(0); send("STREAM START 1\n"); clear(); send("RUN TEXT exclusive\n"); contains("ERR BUSY");
    clear(); poll(1,1); contains("DATA seq=1 ");
    init(0); send("RUN TEXT pressure\n"); data_blocked = true; poll(0,30); equals("");
    send("PING control\n"); equals("PONG control\r\n");
    clear(); data_blocked = false; poll(1,10); contains("BEGIN run=pressure case=TEXT");
    init(0); send("RUN TEXT stopped\nSTOP\n"); clear(); poll(0,10); equals("");
    const char *invalid[] = {"RUN TEXT\n","RUN TEXT invalid!\n","RUN TEXT 123456789012345678901234567890123\n",
        "RUN TEXT token extra\n","RUN unknown token\n","BUILD? extra\n","CAPS? extra\n","SELFTEST extra\n"};
    for (size_t i = 0; i < sizeof(invalid)/sizeof(invalid[0]); ++i) {
        init(0); send(invalid[i]); contains("ERR");
    }
    init(0); send("RUN TEXT 12345678901234567890123456789012\n"); poll(0,10);
    contains("BEGIN run=12345678901234567890123456789012 case=TEXT");
}

static void export_bytes(const char *directory, const char *name) {
    char path[1024];
    int length = snprintf(path,sizeof(path),"%s/%s",directory,name);
    assert(length > 0 && (size_t)length < sizeof(path));
    FILE *file = fopen(path,"wb"); assert(file);
    assert(fwrite(out,1,used,file) == used); assert(fclose(file) == 0);
}

static void export_contract(const char *directory) {
    const char *commands[] = {"BUILD?\n","CAPS?\n","SELFTEST\n"};
    const char *files[] = {"BUILD.txt","CAPS.txt","SELFTEST.txt"};
    for (unsigned i = 0; i < 3; ++i) { init(0); send(commands[i]); export_bytes(directory,files[i]); }
    for (size_t i = 0; i < sizeof(fixture_names)/sizeof(fixture_names[0]); ++i) {
        char command_text[96], file[64];
        snprintf(command_text,sizeof(command_text),"RUN %s native_%s\n",fixture_names[i],fixture_names[i]);
        snprintf(file,sizeof(file),"fixture-%s.bin",fixture_names[i]);
        init(0); send(command_text); poll(0,10); export_bytes(directory,file);
    }
    for (size_t i = 0; i < sizeof(binary_lengths)/sizeof(binary_lengths[0]); ++i) {
        uint8_t p[LAB_MAX_FRAME_BYTES]; char file[64];
        size_t size = boundary_packet(p,binary_lengths[i]);
        init(0); feed_bytes(p,size);
        snprintf(file,sizeof(file),"binary-%lu.bin",(unsigned long)binary_lengths[i]);
        export_bytes(directory,file);
    }
    puts("Exported actual C protocol responses for independent PC contract verification.");
}

int main(int argc, char **argv) {
    assert(lab_crc16((const uint8_t *)"123456789",9) == 0x29b1); ++checks;
    text_tests(); scheduling_tests(); generator_tests(); binary_tests(); malformed_tests();
    monitor_tests();
    fixture_tests();
    large_binary_tests();
    printf("PASS: %u protocol checks (text, timing/wrap, bounded backpressure, faults, CRC, malformed recovery)\n", checks);
    if (argc == 3 && !strcmp(argv[1],"--export")) export_contract(argv[2]);
    return 0;
}
