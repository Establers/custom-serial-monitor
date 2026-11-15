#include "lab_master.h"
#include <string.h>

/* Independent fixed RTU vectors, including wire CRCs. PC definitions start with holding[0]=18. */
static const uint8_t write6[] = {1,6,0,0,0,100,0x88,0x21};
static const uint8_t mask22[] = {1,22,0,0,0,0xf2,0,0x25,0x96,0x2e};
static const uint8_t read1[] = {1,3,0,0,0,1,0x84,0x0a};
static const uint8_t value23[] = {1,3,2,0,0x17,0xf8,0x4a};
static const uint8_t write23[] = {1,23,0,0,0,2,0,0,0,2,4,0x12,0x34,0xab,0xcd,0x3d,0x53};
static const uint8_t value_pair23[] = {1,23,4,0x12,0x34,0xab,0xcd,3,0x34};
static const uint8_t read2[] = {1,3,0,0,0,2,0xc4,0x0b};
static const uint8_t value_pair3[] = {1,3,4,0x12,0x34,0xab,0xcd,0,0x20};
static const uint8_t fifo24[] = {1,24,0,0,0x81,0xdf};
static const uint8_t illegal24[] = {1,0x98,1,0x8a,0};
typedef struct { const uint8_t *tx, *rx; size_t tx_length, rx_length; } step;
#define STEP(tx,rx) {tx,rx,sizeof(tx),sizeof(rx)}
static const step advanced[] = {STEP(mask22,mask22),STEP(read1,value23),STEP(write23,value_pair23),
    STEP(read2,value_pair3),STEP(fifo24,illegal24)};
static const step retry6[] = {STEP(write6,write6),STEP(write6,write6)};
static const step retry22[] = {STEP(mask22,mask22),STEP(mask22,mask22)};
static lab_master_status status;
static const step *steps;
static size_t step_count, rx_position;
static uint32_t next_tx, deadline, first_tx;
static bool active, retry;

void lab_master_reset(void) {
    memset(&status,0,sizeof(status)); status.name = "NONE"; status.result = "IDLE";
    steps = NULL; step_count = rx_position = 0; active = retry = false;
}
bool lab_master_active(void) { return active; }
lab_master_status lab_master_get_status(void) { return status; }
bool lab_master_start(const char *name, uint32_t now) {
    if (active || !name) return false;
    const step *selected;
    size_t count;
    const char *selected_name;
    if (!strcmp(name,"RETRY6")) { selected=retry6; count=2; selected_name="RETRY6"; }
    else if (!strcmp(name,"RETRY22")) { selected=retry22; count=2; selected_name="RETRY22"; }
    else if (!strcmp(name,"ADVANCED")) { selected=advanced; count=5; selected_name="ADVANCED"; }
    else return false;
    lab_master_reset(); steps=selected; step_count=count;
    status.name=selected_name; status.result="RUNNING";
    active=true; retry=selected!=advanced;
    next_tx=now+1000u; deadline=now+8000u;
    return true;
}
static void fail(const char *reason) { active=false; status.result=reason; }
void lab_master_poll(uint32_t now, bool (*write)(const uint8_t *, size_t)) {
    if (!active) return;
    if ((int32_t)(now-deadline)>=0) { fail("TIMEOUT"); return; }
    if (status.sent>=step_count || (int32_t)(now-next_tx)<0) return;
    if (!retry && status.sent!=status.received) return;
    const step *s=&steps[status.sent];
    /* A rejected queue write is a failure, never an automatic resend. */
    if (!write(s->tx,s->tx_length)) { fail("TX_REJECTED"); return; }
    if (status.sent==0) first_tx=now;
    else if (retry) status.retry_ms=now-first_tx;
    ++status.sent; next_tx=now+211u;
}
void lab_master_receive(uint8_t byte, uint32_t now) {
    if (!active) return;
    if (status.received>=status.sent) { fail("UNEXPECTED_RX"); return; }
    const step *s=&steps[status.received];
    if (byte!=s->rx[rx_position]) { fail("RESPONSE_MISMATCH"); return; }
    if (++rx_position<s->rx_length) return;
    rx_position=0; ++status.received;
    if (status.received==1) status.first_ms=now-first_tx;
    if (status.received==2) status.second_ms=now-first_tx;
    if (status.received==step_count) { active=false; status.result="PASS"; }
    else if (!retry) next_tx=now+10u;
}
