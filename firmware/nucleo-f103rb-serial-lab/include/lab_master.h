#ifndef SERIAL_LAB_MASTER_H
#define SERIAL_LAB_MASTER_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Bounded UART master fixtures, explicitly armed from LAB. Never active at boot. */
typedef struct {
    const char *name, *result;
    uint32_t sent, received, retry_ms, first_ms, second_ms;
} lab_master_status;
void lab_master_reset(void);
bool lab_master_start(const char *name, uint32_t now);
bool lab_master_active(void);
void lab_master_poll(uint32_t now, bool (*write)(const uint8_t *, size_t));
void lab_master_receive(uint8_t byte, uint32_t now);
lab_master_status lab_master_get_status(void);
#endif
