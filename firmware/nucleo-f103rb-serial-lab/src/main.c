#include "stm32f1xx.h"
#include "lab.h"
#include <string.h>

#define RX_CAP 2048u
#define TX_CAP 4096u
_Static_assert(RX_CAP >= LAB_MAX_FRAME_BYTES,"RX ring must hold a complete maximum frame");
_Static_assert(LAB_TX_CONTROL_RESERVE >= LAB_MAX_FRAME_BYTES,"Control reserve must fit a maximum binary reply");
_Static_assert(TX_CAP > LAB_TX_CONTROL_RESERVE,"TX queue must leave room for background data");
static uint8_t rx[RX_CAP], tx[TX_CAP];
static volatile uint32_t rx_head, rx_tail, tx_head, tx_tail;
static volatile uint32_t milliseconds, rx_drops, uart_errors;
static uint32_t uart_clock = 32000000u;

/* No constructors; startup still calls newlib's standard init-array entry. */
void _init(void) { }
void _fini(void) { }

void SysTick_Handler(void) { ++milliseconds; }

void USART2_IRQHandler(void) {
    uint32_t status = USART2->SR;
    if (status & (USART_SR_RXNE | USART_SR_ORE | USART_SR_FE | USART_SR_NE | USART_SR_PE)) {
        uint8_t byte = (uint8_t)USART2->DR; /* SR then DR clears receive error flags. */
        if (status & (USART_SR_ORE | USART_SR_FE | USART_SR_NE | USART_SR_PE)) ++uart_errors;
        if (status & USART_SR_RXNE) {
            uint32_t head = rx_head;
            if ((uint32_t)(head - rx_tail) < RX_CAP) {
                rx[head & (RX_CAP - 1)] = byte; __DMB(); rx_head = head + 1;
            } else ++rx_drops;
        }
    }
    if ((status & USART_SR_TXE) && (USART2->CR1 & USART_CR1_TXEIE)) {
        uint32_t tail = tx_tail;
        if (tail != tx_head) {
            USART2->DR = tx[tail & (TX_CAP - 1)]; __DMB(); tx_tail = tail + 1;
        } else USART2->CR1 &= ~USART_CR1_TXEIE;
    }
}

static bool uart_write(const uint8_t *bytes, size_t length) {
    uint32_t head = tx_head;
    if (length > TX_CAP - (uint32_t)(head - tx_tail)) return false;
    for (size_t i = 0; i < length; ++i) tx[(head + i) & (TX_CAP - 1)] = bytes[i];
    /* Publish only after the entire record is copied. ISR never sees partial records. */
    __DMB();
    uint32_t mask = __get_PRIMASK(); __disable_irq();
    tx_head = head + (uint32_t)length;
    USART2->CR1 |= USART_CR1_TXEIE;
    __set_PRIMASK(mask);
    return true;
}

static bool uart_write_data(const uint8_t *bytes, size_t length) {
    /* Reserve a full 1KiB-payload reply plus room for STOP/status. */
    if (length + LAB_TX_CONTROL_RESERVE > TX_CAP - (uint32_t)(tx_head - tx_tail)) return false;
    return uart_write(bytes, length);
}

static void led(bool on) { GPIOA->BSRR = on ? (1u << 5) : (1u << 21); }
static bool button(void) { return (GPIOC->IDR & (1u << 13)) == 0; }
static void stats(uint32_t *drops, uint32_t *errors) { *drops = rx_drops; *errors = uart_errors; }
static bool reset(void) {
    if (tx_head != tx_tail || !(USART2->SR & USART_SR_TC)) return false;
    NVIC_SystemReset();
    return true;
}
static bool baud(uint32_t rate) {
    /* Deferred by lab for 500ms. Do not change BRR before the old-rate ACK drains. */
    if (tx_head != tx_tail || !(USART2->SR & USART_SR_TC)) return false;
    USART2->CR1 &= ~USART_CR1_UE;
    USART2->BRR = (uart_clock + rate / 2u) / rate;
    USART2->CR1 |= USART_CR1_UE;
    return true;
}

static void hardware_init(void) {
    /* HSI/2 * 16 = 64MHz: independent of Nucleo HSE/MCO solder bridge revision. */
    RCC->CR |= RCC_CR_HSION;
    for (uint32_t i = 0; !(RCC->CR & RCC_CR_HSIRDY) && i < 1000000u; ++i) { }
    FLASH->ACR = FLASH_ACR_PRFTBE | FLASH_ACR_LATENCY_2;
    RCC->CFGR = RCC_CFGR_PLLMULL16 | RCC_CFGR_PPRE1_DIV2;
    RCC->CR |= RCC_CR_PLLON;
    uint32_t wait = 0;
    while (!(RCC->CR & RCC_CR_PLLRDY) && ++wait < 1000000u) { }
    if (RCC->CR & RCC_CR_PLLRDY) {
        RCC->CFGR |= RCC_CFGR_SW_PLL;
        wait = 0;
        while ((RCC->CFGR & RCC_CFGR_SWS) != RCC_CFGR_SWS_PLL && ++wait < 1000000u) { }
    }
    SystemCoreClockUpdate();
    uart_clock = SystemCoreClock / 2u;
    SCB->VTOR = FLASH_BASE;
    RCC->APB2ENR |= RCC_APB2ENR_IOPAEN | RCC_APB2ENR_IOPCEN | RCC_APB2ENR_AFIOEN;
    RCC->APB1ENR |= RCC_APB1ENR_USART2EN;
    /* PA2 TX alternate push-pull 50MHz; PA3 RX floating; PA5 LD2 push-pull. */
    GPIOA->CRL = (GPIOA->CRL & ~((15u << 8) | (15u << 12) | (15u << 20))) |
                  (11u << 8) | (4u << 12) | (2u << 20);
    GPIOC->CRH = (GPIOC->CRH & ~(15u << 20)) | (8u << 20);
    GPIOC->BSRR = 1u << 13;
    USART2->BRR = (uart_clock + 57600u) / 115200u;
    USART2->CR1 = USART_CR1_UE | USART_CR1_TE | USART_CR1_RE | USART_CR1_RXNEIE;
    NVIC_SetPriority(USART2_IRQn, 1);
    NVIC_EnableIRQ(USART2_IRQn);
    (void)SysTick_Config(SystemCoreClock / 1000u);
    led(false);
}

int main(void) {
    hardware_init();
    const lab_platform platform = {uart_write, uart_write_data, led, button, baud, reset, stats};
    lab_init(&platform, milliseconds);
    for (;;) {
        /* Bound parser work so generators, STOP, and delayed output keep progressing. */
        for (unsigned i = 0; i < 128 && rx_tail != rx_head; ++i) {
            uint32_t tail = rx_tail;
            uint8_t byte = rx[tail & (RX_CAP - 1)]; __DMB(); rx_tail = tail + 1;
            lab_receive(byte, milliseconds);
        }
        lab_poll(milliseconds);
        /* Tick and USART interrupts wake the MCU; no polling delays or heap. */
        __WFI();
    }
}
