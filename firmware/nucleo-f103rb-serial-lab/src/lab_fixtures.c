#include "lab_fixtures.h"
#include <string.h>

typedef struct { const char *name; const uint8_t *bytes; size_t length; } fixture;
static const uint8_t text[] = "alpha  beta\r\nGamma 123 !@#\r\n";
static const uint8_t utf8[] = "\xEC\x95\x88\xEB\x85\x95\xED\x95\x98\xEC\x84\xB8\xEC\x9A\x94 \xE6\x97\xA5\xE6\x9C\xAC\xE8\xAA\x9E \xF0\x9F\x9A\x80\r\n";
static const uint8_t cp949[] = {0xbe,0xc8,0xb3,0xe7,0xc7,0xcf,0xbc,0xbc,0xbf,0xe4,13,10};
static const uint8_t newlines[] = "CRLF\r\nLF\nCR\rEND\r\n";
static const uint8_t events[] = "[INFO] before\r\n[WARN] sensor=mock\r\n[ERROR] fault=mock\r\n[INFO] after\r\n";
static const uint8_t json[] = "{\"sensor\":\"mock\",\"seq\":1,\"ok\":true}\r\n";
static const uint8_t ansi[] = "\x1b[32mGREEN\x1b[0m \x1b[31mRED\x1b[0m\r\n";
/* Intentionally contains NUL, TAB and backspace, but no clear-screen sequence. */
static const uint8_t control[] = {'A',9,'B',8,'C',0,'D',13,10};
#define ENTRY(id, value) {id, value, sizeof(value) - 1}
static const fixture cases[] = {
    ENTRY("TEXT",text), ENTRY("UTF8",utf8), {"CP949",cp949,sizeof(cp949)},
    ENTRY("NEWLINES",newlines), ENTRY("EVENTS",events), ENTRY("JSON",json),
    ENTRY("ANSI",ansi), {"CONTROL",control,sizeof(control)}
};

size_t lab_fixture_copy(const char *name, uint8_t *destination, size_t capacity) {
    if (!strcmp(name,"BYTES")) {
        if (capacity < 256) return 0;
        for (size_t i = 0; i < 256; ++i) destination[i] = (uint8_t)i;
        return 256;
    }
    for (size_t i = 0; i < sizeof(cases)/sizeof(cases[0]); ++i) {
        if (!strcmp(name,cases[i].name)) {
            if (capacity < cases[i].length) return 0;
            memcpy(destination,cases[i].bytes,cases[i].length);
            return cases[i].length;
        }
    }
    return 0;
}
