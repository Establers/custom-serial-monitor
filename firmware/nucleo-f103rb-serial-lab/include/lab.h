#ifndef SERIAL_LAB_H
#define SERIAL_LAB_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define LAB_MAX_PAYLOAD 1024u
#define LAB_FRAME_OVERHEAD 11u
#define LAB_MAX_FRAME_BYTES (LAB_MAX_PAYLOAD + LAB_FRAME_OVERHEAD)
#define LAB_TX_CONTROL_RESERVE 1536u

typedef struct {
    /* Main-loop only. Must enqueue the entire buffer or return false. */
    bool (*write)(const uint8_t *bytes, size_t length);
    bool (*write_data)(const uint8_t *bytes, size_t length); /* reserve room for control replies */
    void (*led)(bool on);
    bool (*button)(void);
    bool (*baud)(uint32_t rate); /* false until old-rate TX has fully drained */
    bool (*reset)(void); /* false until the reset acknowledgement drains */
    void (*stats)(uint32_t *rx_drops, uint32_t *uart_errors);
} lab_platform;

void lab_init(const lab_platform *platform, uint32_t now);
void lab_receive(uint8_t byte, uint32_t now);
void lab_poll(uint32_t now);
uint16_t lab_crc16(const uint8_t *bytes, size_t length);
#endif
