#include "lab.h"
#include "lab_fixtures.h"
#ifdef LAB_TARGET_BUILD
#include "lab_build_info.h"
#else
#define LAB_BUILD_ID "host-test"
#endif
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LINE_CAP 512u
#define FRAME_PAYLOAD LAB_MAX_PAYLOAD
#define FRAME_CAP LAB_MAX_FRAME_BYTES

static lab_platform io;
static char line[LINE_CAP];
static size_t line_len;
static bool line_discard, swallow_lf;
static uint8_t frame[FRAME_CAP];
static size_t frame_len, frame_expected;
static uint32_t last_byte, commands, parse_errors, tx_rejected;
static uint32_t baud_rate, baud_pending, baud_due, reset_due;
static bool reset_pending;
static uint32_t stream_interval, stream_due, stream_remaining, stream_seq;
static uint32_t burst_remaining, burst_seq, burst_padding;
static uint32_t hex_remaining, hex_seq;
static uint32_t drop_every, dup_every, crc_every;
static uint32_t records, injected_drops, injected_duplicates, injected_crc;
static uint8_t pending[2200];
static size_t pending_len, pending_pos, pending_chunk;
static uint32_t pending_due, pending_gap;
static bool monitor_enabled, button_raw, button_stable;
static uint32_t heartbeat_due, heartbeat_seq, button_changed_at, button_pressed_at;
static uint32_t button_presses, button_releases, monitor_missed;
#define HEARTBEAT_MS 1000u
#define BUTTON_DEBOUNCE_MS 20u

static bool due(uint32_t now, uint32_t deadline) {
    return (int32_t)(now - deadline) >= 0;
}

uint16_t lab_crc16(const uint8_t *bytes, size_t length) {
    uint16_t crc = 0xffff;
    for (size_t i = 0; i < length; ++i) {
        crc ^= (uint16_t)bytes[i] << 8;
        for (unsigned bit = 0; bit < 8; ++bit)
            crc = (crc & 0x8000) ? (uint16_t)((crc << 1) ^ 0x1021) : (uint16_t)(crc << 1);
    }
    return crc;
}

static bool output(const void *data, size_t length) {
    if (io.write((const uint8_t *)data, length)) return true;
    ++tx_rejected;
    return false;
}

static bool say(const char *format, ...) {
    char text[768];
    va_list args;
    va_start(args, format);
    int count = vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    if (count < 0 || (size_t)count >= sizeof(text)) {
        ++parse_errors;
        return false;
    }
    return output(text, (size_t)count);
}

static void error(const char *reason) { (void)say("ERR %s\r\n", reason); }

/* Background diagnostics must not consume the reserved command-reply capacity. */
static void monitor_say(const char *format, ...) {
    char text[256];
    va_list args;
    va_start(args, format);
    int count = vsnprintf(text, sizeof(text), format, args);
    va_end(args);
    if (count < 0 || (size_t)count >= sizeof(text) ||
        !io.write_data((const uint8_t *)text, (size_t)count)) ++monitor_missed;
}

static void poll_monitor(uint32_t now) {
    bool pressed = io.button();
    if (pressed != button_raw) {
        button_raw = pressed;
        button_changed_at = now;
    }
    if (button_stable != button_raw && due(now, button_changed_at + BUTTON_DEBOUNCE_MS)) {
        button_stable = button_raw;
        if (button_stable) {
            button_pressed_at = now;
            ++button_presses;
            if (monitor_enabled) monitor_say("INFO USER PRESS count=%lu uptime_ms=%lu\r\n",
                (unsigned long)button_presses, (unsigned long)now);
        } else {
            ++button_releases;
            if (monitor_enabled) monitor_say("INFO USER RELEASE count=%lu held_ms=%lu uptime_ms=%lu\r\n",
                (unsigned long)button_releases, (unsigned long)(now - button_pressed_at), (unsigned long)now);
        }
    }
    if (monitor_enabled && due(now, heartbeat_due)) {
        uint32_t drops = 0, errors = 0;
        io.stats(&drops, &errors);
        ++heartbeat_seq;
        monitor_say("INFO HEARTBEAT seq=%lu uptime_ms=%lu user=%s presses=%lu releases=%lu rx_drops=%lu uart_errors=%lu missed=%lu\r\n",
            (unsigned long)heartbeat_seq, (unsigned long)now, button_stable ? "PRESSED" : "RELEASED",
            (unsigned long)button_presses, (unsigned long)button_releases,
            (unsigned long)drops, (unsigned long)errors, (unsigned long)monitor_missed);
        /* No catch-up burst after a pause or a full TX queue. */
        heartbeat_due = now + HEARTBEAT_MS;
    }
}

static void stop_jobs(void) {
    monitor_enabled = false;
    stream_interval = stream_remaining = burst_remaining = hex_remaining = 0;
    pending_len = pending_pos = 0;
}

static bool number(const char *text, uint32_t low, uint32_t high, uint32_t *result) {
    if (!text || !*text) return false;
    uint32_t value = 0;
    for (; *text; ++text) {
        if (*text < '0' || *text > '9') return false;
        unsigned digit = (unsigned)(*text - '0');
        if (digit > high || value > (high - digit) / 10u) return false;
        value = value * 10u + digit;
    }
    if (value < low || value > high) return false;
    *result = value;
    return true;
}

static char *token(char **cursor) {
    char *start = *cursor;
    while (*start == ' ') ++start;
    if (!*start) { *cursor = start; return NULL; }
    char *end = strchr(start, ' ');
    if (end) { *end = 0; *cursor = end + 1; }
    else *cursor = start + strlen(start);
    return start;
}

static uint32_t get32(const uint8_t *p) {
    return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24;
}

static size_t make_frame(uint8_t *out, uint8_t type, uint32_t seq,
                         const uint8_t *payload, size_t length, bool corrupt) {
    out[0] = 0xa5; out[1] = 0x5a; out[2] = type;
    for (unsigned i = 0; i < 4; ++i) out[3 + i] = (uint8_t)(seq >> (8u * i));
    out[7] = (uint8_t)length; out[8] = (uint8_t)(length >> 8);
    if (length) memcpy(out + 9, payload, length);
    uint16_t crc = lab_crc16(out + 2, 7 + length);
    if (corrupt) { crc ^= 1; ++injected_crc; }
    out[9 + length] = (uint8_t)crc;
    out[10 + length] = (uint8_t)(crc >> 8);
    return length + 11;
}

static void binary_reply(uint8_t type, uint32_t seq, const uint8_t *payload, size_t length) {
    uint8_t response[FRAME_CAP];
    size_t count = make_frame(response, type, seq, payload, length, false);
    (void)output(response, count);
}

static void process_frame(void) {
    uint32_t seq = get32(frame + 3);
    size_t length = (size_t)frame[7] | (size_t)frame[8] << 8;
    uint16_t actual = (uint16_t)frame[9 + length] | (uint16_t)frame[10 + length] << 8;
    if (lab_crc16(frame + 2, 7 + length) != actual) {
        ++parse_errors;
        binary_reply(0xe0, seq, (const uint8_t *)"CRC", 3);
    } else if (frame[2] == 1 || frame[2] == 2) {
        ++commands;
        binary_reply((uint8_t)(frame[2] | 0x80), seq, frame + 9, length);
    } else if (frame[2] == 3 && length == 0) {
        ++commands;
        binary_reply(0x83, seq, (const uint8_t *)"NUCLEO-F103RB SERIAL-LAB/1", 26);
    } else {
        ++parse_errors;
        binary_reply(0xe0, seq, (const uint8_t *)"TYPE", 4);
    }
}

static void start_pending(size_t length, size_t chunk, uint32_t when, uint32_t gap) {
    pending_len = length; pending_pos = 0; pending_chunk = chunk;
    pending_due = when; pending_gap = gap;
}

static bool schedule(const void *data, size_t length, size_t chunk, uint32_t when, uint32_t gap) {
    if (pending_len) { error("BUSY pending-output; use STOP"); return false; }
    if (length > sizeof(pending)) { error("SIZE"); return false; }
    memcpy(pending, data, length);
    start_pending(length,chunk,when,gap);
    return true;
}

static void run_fixture(char *arguments, uint32_t now) {
    char *name = token(&arguments), *run = token(&arguments);
    if (!name || !run || *arguments || strlen(run) > 32) {
        error("ARGS RUN case run_id=1..32_ASCII"); return;
    }
    for (const char *p = run; *p; ++p) {
        if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') ||
              (*p >= '0' && *p <= '9') || *p == '_' || *p == '-')) {
            error("ARGS invalid run_id"); return;
        }
    }
    /* Deterministic fixtures require exclusive device output, without stopping
       jobs owned by another operator or silently changing fault settings. */
    if (stream_interval || burst_remaining || hex_remaining || pending_len || baud_pending || reset_pending ||
        drop_every || dup_every || crc_every) { error("BUSY RUN requires idle and FAULT CLEAR"); return; }
    uint8_t payload[256], packet[512];
    size_t length = lab_fixture_copy(name,payload,sizeof(payload));
    if (!length) { error("CASE unknown; CAPS?"); return; }
    int prefix = snprintf((char *)packet,sizeof(packet),
        "BEGIN run=%s case=%s len=%lu crc16=%04X build=%s\r\n",
        run,name,(unsigned long)length,(unsigned)lab_crc16(payload,length),LAB_BUILD_ID);
    if (prefix < 0 || (size_t)prefix + length >= sizeof(packet)) { error("SIZE"); return; }
    memcpy(packet + prefix,payload,length);
    size_t offset = (size_t)prefix + length;
    int suffix = snprintf((char *)packet + offset,sizeof(packet) - offset,
        "\r\nEND run=%s case=%s\r\n",run,name);
    if (suffix < 0 || (size_t)suffix >= sizeof(packet) - offset) { error("SIZE"); return; }
    (void)schedule(packet,offset + (size_t)suffix,128,now,0);
}

static void selftest(void) {
    /* Reference values, no GPIO/baud/reset/job mutations and no dynamic allocation.
       This proves internal primitives only, not the PC/USB/UART path. */
    uint8_t values[256] = {0};
    bool crc = lab_crc16((const uint8_t *)"123456789",9) == 0x29b1 && lab_crc16(NULL,0) == 0xffff;
    bool fixtures = lab_fixture_copy("BYTES",values,sizeof(values)) == 256;
    for (unsigned i = 0; i < 256; ++i) if (values[i] != (uint8_t)i) fixtures = false;
    bool limits = sizeof(pending) >= 2050 && FRAME_CAP == 1035 && LINE_CAP == 512 &&
                  LAB_TX_CONTROL_RESERVE >= FRAME_CAP;
    (void)say("SELFTEST %s crc=%s fixtures=%s limits=%s build=%s\r\n",
        crc && fixtures && limits ? "PASS" : "FAIL",crc ? "PASS" : "FAIL",
        fixtures ? "PASS" : "FAIL",limits ? "PASS" : "FAIL",LAB_BUILD_ID);
}

static void command(uint32_t now) {
    ++commands;
    if (!strncmp(line, "ECHO ", 5)) { (void)say("ECHO %s\r\n", line + 5); return; }
    if (!strcmp(line, "PING") || !strncmp(line, "PING ", 5)) {
        (void)say("PONG%s\r\n", line + 4); return;
    }
    char *cursor = line;
    char *cmd = token(&cursor);
    if (!cmd) return;
    if (!strcmp(cmd, "HELP") && !*cursor) {
        (void)say("SERIAL-LAB/1 commands (uppercase; CR/LF/CRLF)\r\n"
                  "PING [token] | ECHO text | ID? | STATUS? | STOP\r\n"
                  "MONITOR ON|OFF | MONITOR? (USER events + 1Hz heartbeat)\r\n"
                  "BUILD? | CAPS? | SELFTEST | RUN case run_id\r\n"
                  "STREAM START interval_ms [count] | STREAM STOP\r\n"
                  "BURST count [padding_0..128] | HEX count\r\n"
                  "DELAY ms text | SPLIT gap_ms text | LONG 1..2048\r\n"
                  "UTF8 | ANSI | NEWLINES | LEVELS | SILENT\r\n"
                  "FAULT DROP|DUP|CRC every_n | FAULT CLEAR\r\n"
                  "LED ON|OFF | BUTTON? | BAUD 9600|19200|38400|57600|115200|230400|460800|921600\r\n"
                  "RESET CONFIRM (explicit software reset)\r\n");
    } else if (!strcmp(cmd, "ID?") && !*cursor) {
        (void)say("ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1\r\n");
    } else if (!strcmp(cmd, "BUILD?") && !*cursor) {
        (void)say("BUILD version=%s protocol=1 id=%s\r\n",LAB_FIRMWARE_VERSION,LAB_BUILD_ID);
    } else if (!strcmp(cmd, "CAPS?") && !*cursor) {
        (void)say("CAPS protocol=1 version=%s build=%s line_max=511 payload_max=%u long_max=2048 fixture_count=%u fixtures=%s\r\n",
            LAB_FIRMWARE_VERSION,LAB_BUILD_ID,(unsigned)LAB_MAX_PAYLOAD,LAB_FIXTURE_COUNT,LAB_FIXTURE_NAMES);
    } else if (!strcmp(cmd, "SELFTEST") && !*cursor) {
        selftest();
    } else if (!strcmp(cmd, "RUN")) {
        run_fixture(cursor,now);
    } else if (!strcmp(cmd, "STATUS?") && !*cursor) {
        uint32_t drops = 0, errors = 0;
        io.stats(&drops, &errors);
        (void)say("STATUS tick=%lu baud=%lu commands=%lu parse_errors=%lu rx_drops=%lu uart_errors=%lu tx_rejected=%lu records=%lu injected_drop=%lu injected_dup=%lu injected_crc=%lu\r\n",
            (unsigned long)now, (unsigned long)baud_rate, (unsigned long)commands,
            (unsigned long)parse_errors, (unsigned long)drops, (unsigned long)errors,
            (unsigned long)tx_rejected, (unsigned long)records, (unsigned long)injected_drops,
            (unsigned long)injected_duplicates, (unsigned long)injected_crc);
    } else if (!strcmp(cmd, "STOP") && !*cursor) {
        stop_jobs(); (void)say("OK STOP\r\n");
    } else if (!strcmp(cmd, "MONITOR?" ) && !*cursor) {
        (void)say("MONITOR enabled=%u heartbeat_ms=1000 debounce_ms=20 user=%s presses=%lu releases=%lu missed=%lu\r\n",
            monitor_enabled ? 1u : 0u, button_stable ? "PRESSED" : "RELEASED",
            (unsigned long)button_presses, (unsigned long)button_releases, (unsigned long)monitor_missed);
    } else if (!strcmp(cmd, "MONITOR")) {
        char *state = token(&cursor);
        if (state && !*cursor && (!strcmp(state, "ON") || !strcmp(state, "OFF"))) {
            monitor_enabled = !strcmp(state, "ON");
            heartbeat_due = now + HEARTBEAT_MS;
            (void)say("OK MONITOR %s\r\n", state);
        } else error("ARGS MONITOR ON|OFF");
    } else if (!strcmp(cmd, "STREAM")) {
        char *action = token(&cursor);
        uint32_t interval, count = 0;
        if (action && !strcmp(action, "STOP") && !*cursor) {
            stream_interval = 0; (void)say("OK STREAM STOP\r\n");
        } else if (action && !strcmp(action, "START") &&
                   number(token(&cursor), 1, 60000, &interval)) {
            char *count_text = token(&cursor);
            if ((count_text && !number(count_text, 1, 1000000, &count)) || *cursor)
                error("ARGS STREAM START interval_ms [count]");
            else {
                stream_interval = interval; stream_remaining = count;
                stream_seq = 0; stream_due = now + interval;
                (void)say("OK STREAM interval=%lu count=%lu\r\n", (unsigned long)interval, (unsigned long)count);
            }
        } else error("ARGS STREAM START interval_ms [count] | STREAM STOP");
    } else if (!strcmp(cmd, "BURST") || !strcmp(cmd, "HEX")) {
        uint32_t count, padding = 0;
        if (!number(token(&cursor), 1, 1000000, &count)) { error("ARGS count=1..1000000"); return; }
        char *pad = token(&cursor);
        if ((pad && (strcmp(cmd, "BURST") || !number(pad, 0, 128, &padding))) || *cursor) {
            error("ARGS BURST count [padding] | HEX count"); return;
        }
        if (!strcmp(cmd, "BURST")) {
            burst_remaining = count; burst_seq = 0; burst_padding = padding;
            (void)say("OK BURST count=%lu\r\n", (unsigned long)count);
        } else {
            hex_remaining = count; hex_seq = 0;
            (void)say("OK HEX count=%lu\r\n", (unsigned long)count);
        }
    } else if (!strcmp(cmd, "DELAY") || !strcmp(cmd, "SPLIT")) {
        uint32_t delay;
        if (!number(token(&cursor), 1, 60000, &delay) || !*cursor) {
            error("ARGS DELAY|SPLIT 1..60000 text"); return;
        }
        uint8_t text[LINE_CAP + 2];
        size_t length = strlen(cursor);
        memcpy(text, cursor, length); text[length++] = '\r'; text[length++] = '\n';
        bool split = !strcmp(cmd, "SPLIT");
        (void)schedule(text, length, split ? 1 : length, now + delay, split ? delay : 0);
    } else if (!strcmp(cmd, "LONG")) {
        uint32_t count;
        if (!number(token(&cursor), 1, 2048, &count) || *cursor) { error("ARGS LONG 1..2048"); return; }
        if (pending_len) { error("BUSY pending-output; use STOP"); return; }
        /* Generate directly in the existing bounded job buffer, avoiding a 2KiB
           automatic array on the MCU's 4KiB stack. Publish only after filling. */
        for (uint32_t i = 0; i < count; ++i) pending[i] = (uint8_t)('A' + i % 26);
        pending[count] = '\r'; pending[count + 1] = '\n';
        start_pending(count + 2,128,now,0);
    } else if (!strcmp(cmd, "UTF8") && !*cursor) {
        /* Explicit UTF-8 bytes keep MSVC and ARM compiler encodings identical. */
        (void)say("UTF8 \xEC\x95\x88\xEB\x85\x95\xED\x95\x98\xEC\x84\xB8\xEC\x9A\x94 \xE6\x97\xA5\xE6\x9C\xAC\xE8\xAA\x9E \xF0\x9F\x9A\x80\r\n");
    } else if (!strcmp(cmd, "ANSI") && !*cursor) {
        (void)say("ANSI \x1b[32mGREEN\x1b[0m \x1b[31mRED\x1b[0m\r\n");
    } else if (!strcmp(cmd, "NEWLINES") && !*cursor) {
        (void)say("CRLF\r\nLF\nCR\rEND\r\n");
    } else if (!strcmp(cmd, "LEVELS") && !*cursor) {
        (void)say("[INFO] normal\r\n[WARN] test warning\r\n[ERROR] test error\r\n[INFO] recovered\r\n");
    } else if (!strcmp(cmd, "SILENT") && !*cursor) {
        /* Intentionally no reply; background jobs may still produce data. */
    } else if (!strcmp(cmd, "FAULT")) {
        char *kind = token(&cursor);
        uint32_t every;
        if (kind && !strcmp(kind, "CLEAR") && !*cursor) {
            drop_every = dup_every = crc_every = 0; (void)say("OK FAULT CLEAR\r\n");
        } else if (kind && number(token(&cursor), 2, 1000000, &every) && !*cursor) {
            if (!strcmp(kind, "DROP")) drop_every = every;
            else if (!strcmp(kind, "DUP")) dup_every = every;
            else if (!strcmp(kind, "CRC")) crc_every = every;
            else { error("ARGS FAULT DROP|DUP|CRC n | CLEAR"); return; }
            (void)say("OK FAULT %s %lu\r\n", kind, (unsigned long)every);
        } else error("ARGS FAULT DROP|DUP|CRC n>=2 | CLEAR");
    } else if (!strcmp(cmd, "LED")) {
        char *state = token(&cursor);
        if (state && (!strcmp(state, "ON") || !strcmp(state, "OFF")) && !*cursor) {
            io.led(!strcmp(state, "ON")); (void)say("OK LED %s\r\n", state);
        } else error("ARGS LED ON|OFF");
    } else if (!strcmp(cmd, "BUTTON?") && !*cursor) {
        (void)say("BUTTON %u\r\n", io.button() ? 1u : 0u);
    } else if (!strcmp(cmd, "BAUD")) {
        uint32_t rate;
        const uint32_t supported[] = {9600,19200,38400,57600,115200,230400,460800,921600};
        if (!number(token(&cursor), 9600, 921600, &rate) || *cursor) { error("ARGS BAUD"); return; }
        bool valid = false;
        for (size_t i = 0; i < sizeof(supported)/sizeof(supported[0]); ++i) if (rate == supported[i]) valid = true;
        if (!valid || baud_pending) { error("BAUD unsupported or pending"); return; }
        stop_jobs();
        if (say("OK BAUD %lu SWITCH_IN_MS=500\r\n", (unsigned long)rate)) {
            baud_pending = rate; baud_due = now + 500;
        }
    } else if (!strcmp(cmd, "RESET") && !strcmp(cursor, "CONFIRM")) {
        stop_jobs();
        if (say("OK RESET\r\n")) { reset_pending = true; reset_due = now + 500; }
    } else {
        ++parse_errors; error("UNKNOWN_OR_ARGS; HELP");
    }
}

void lab_init(const lab_platform *platform, uint32_t now) {
    io = *platform;
    line_len = frame_len = frame_expected = 0;
    line_discard = swallow_lf = false;
    commands = parse_errors = tx_rejected = 0;
    records = injected_drops = injected_duplicates = injected_crc = 0;
    baud_rate = 115200; baud_pending = 0; reset_pending = false;
    drop_every = dup_every = crc_every = 0;
    stop_jobs(); last_byte = now;
    monitor_enabled = true;
    heartbeat_due = now + HEARTBEAT_MS;
    heartbeat_seq = button_presses = button_releases = monitor_missed = 0;
    button_raw = button_stable = io.button();
    button_changed_at = button_pressed_at = now;
    (void)say("BOOT NUCLEO-F103RB SERIAL-LAB/1 baud=115200 8N1\r\n");
    (void)say("READY USER=B1/PC13 heartbeat_ms=1000 debounce_ms=20; MONITOR OFF or STOP pauses output\r\n");
}

void lab_receive(uint8_t byte, uint32_t now) {
    if (frame_len && (uint32_t)(now - last_byte) > 500) {
        frame_len = frame_expected = 0; ++parse_errors;
    }
    last_byte = now;
    if (frame_len) {
        if (frame_len == 1 && byte != 0x5a) {
            ++parse_errors; frame_len = 0;
            if (byte == 0xa5) { frame[frame_len++] = byte; return; }
            /* Re-process the non-header byte as text. */
        } else {
            frame[frame_len++] = byte;
            if (frame_len == 9) {
                size_t length = (size_t)frame[7] | (size_t)frame[8] << 8;
                if (length > FRAME_PAYLOAD) {
                    ++parse_errors;
                    binary_reply(0xe0, get32(frame + 3), (const uint8_t *)"LEN", 3);
                    frame_len = frame_expected = 0;
                    return;
                }
                frame_expected = 11 + length;
            }
            if (frame_expected && frame_len == frame_expected) {
                process_frame(); frame_len = frame_expected = 0;
            }
            return;
        }
    }
    if (byte == 0xa5 && line_len == 0 && !line_discard) {
        swallow_lf = false; frame[0] = byte; frame_len = 1; frame_expected = 0; return;
    }
    if (swallow_lf && byte == '\n') { swallow_lf = false; return; }
    swallow_lf = false;
    if (byte == '\r' || byte == '\n') {
        swallow_lf = byte == '\r';
        if (line_discard) { ++parse_errors; error("LINE_TOO_LONG_OR_CONTROL max=511"); }
        else if (line_len) { line[line_len] = 0; command(now); }
        line_len = 0; line_discard = false;
    } else if (byte < 32 || byte == 127 || line_len >= sizeof(line) - 1) {
        line_discard = true;
    } else if (!line_discard) line[line_len++] = (char)byte;
}

/* A complete record and its intentional duplicate are one atomic queue write. */
static bool numbered(const uint8_t *data, size_t length, uint32_t seq) {
    if (drop_every && seq % drop_every == 0) { ++injected_drops; ++records; return true; }
    if (dup_every && seq % dup_every == 0) {
        uint8_t twice[768];
        if (length * 2 > sizeof(twice)) return false;
        memcpy(twice, data, length); memcpy(twice + length, data, length);
        if (!io.write_data(twice, length * 2)) return false;
        ++injected_duplicates;
    } else if (!io.write_data(data, length)) return false;
    ++records; return true;
}

void lab_poll(uint32_t now) {
    poll_monitor(now);
    if (baud_pending) {
        if (due(now, baud_due) && io.baud(baud_pending)) {
            baud_rate = baud_pending; baud_pending = 0;
            (void)say("READY BAUD %lu\r\n", (unsigned long)baud_rate);
        }
        return;
    }
    if (reset_pending) {
        if (due(now, reset_due) && io.reset()) reset_pending = false;
        return;
    }
    if (pending_len && due(now, pending_due)) {
        size_t count = pending_len - pending_pos;
        if (count > pending_chunk) count = pending_chunk;
        if (io.write_data(pending + pending_pos, count)) {
            pending_pos += count; pending_due = now + pending_gap;
            if (pending_pos == pending_len) pending_pos = pending_len = 0;
        }
    }
    if (stream_interval && due(now, stream_due)) {
        char text[96]; uint32_t seq = stream_seq + 1;
        int count = snprintf(text, sizeof(text), "DATA seq=%lu tick=%lu value=%lu\r\n",
            (unsigned long)seq, (unsigned long)now, (unsigned long)(seq % 1000));
        if (numbered((uint8_t *)text, (size_t)count, seq)) {
            stream_seq = seq; stream_due = now + stream_interval;
            if (stream_remaining && --stream_remaining == 0) stream_interval = 0;
        }
    }
    if (burst_remaining) {
        char text[220]; uint32_t seq = burst_seq + 1;
        int count = snprintf(text, sizeof(text), "BURST seq=%lu payload=", (unsigned long)seq);
        for (uint32_t i = 0; i < burst_padding; ++i) text[count++] = (char)('A' + i % 26);
        text[count++] = '\r'; text[count++] = '\n';
        if (numbered((uint8_t *)text, (size_t)count, seq)) { burst_seq = seq; --burst_remaining; }
    }
    if (hex_remaining) {
        uint8_t data[43], payload[32]; uint32_t seq = hex_seq + 1;
        for (size_t i = 0; i < sizeof(payload); ++i) payload[i] = (uint8_t)(seq + i);
        bool corrupt = crc_every && seq % crc_every == 0;
        /* Only count injected corruptions after successful queue admission. */
        size_t length = make_frame(data, 0x90, seq, payload, sizeof(payload), false);
        if (corrupt) data[length - 2] ^= 1;
        if (numbered(data, length, seq)) {
            if (corrupt && !(drop_every && seq % drop_every == 0)) ++injected_crc;
            hex_seq = seq; --hex_remaining;
        }
    }
}
