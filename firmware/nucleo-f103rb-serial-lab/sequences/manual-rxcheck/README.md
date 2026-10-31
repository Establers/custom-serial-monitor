# COM3 STM32 실물 RX Check 시험

Serial Monitor의 Sequences에서 JSON 불러오기로 두 `.gui.json` 파일을 선택합니다.
불러오기만으로는 장치에 송신하지 않습니다. COM3 / 115200 / 8N1 / Handshake None /
RX UTF-8 상태에서 시퀀스를 선택하고 Run을 누릅니다. RepeatCount는 각각 1입니다.
각 단계에 Terminal/HEX 모드와 줄 끝이 지정되어 있어 메인 모드를 바꿀 필요가 없습니다.

| 시퀀스 | 예상 시간 | 정상 결과 |
| --- | --- | --- |
| `COM3 STM32 - RX Check 정상 시험` | 약 11초 | RX Check 10개 모두 일치 |
| `COM3 STM32 - RX Check 타임아웃 시험 (미일치 1개 정상)` | 약 3초 | RX Check 3개 중 일치 2개, 의도한 미일치 1개 |

정상 시험은 STOP, 펌웨어 ID, 빠른 PONG, LED ON/OFF, UTF-8 바이트 분할 응답,
800ms 지연 응답, CRC 포함 HEX 응답, HEX `??` 와일드카드, 누적 UART 오류를 확인합니다.
초록 LD2가 약 1.5초 켜졌다 꺼지는지 실물에서도 확인하세요.
Results에서 각 RX Check의 일치 여부, 실제 RX, 시간을 확인할 수 있습니다.

타임아웃 시험은 `SILENT`에 응답이 없어 1000ms 후 한 항목이 미일치해야 합니다.
이후 `PING MANUAL_RX_RECOVER`가 실행되고 `PONG MANUAL_RX_RECOVER`가 일치해야 합니다.
전체 결과에 미일치가 포함된 것으로 표시되어도 이 시험에서는 예상한 결과입니다.

두 시퀀스는 첫 STOP에서 기존 출력 job을 종료합니다. UART 오류 카운터는 부팅 이후 누적이며,
기존 오류가 있다면 정상 시험 마지막 항목이 미일치할 수 있습니다. 실제 STATUS 응답을 확인하세요.
HEX 응답 검사에는 길이·payload·CRC의 고정 바이트를 사용하지만 전체 장시간 수신 무결성 검사를 대신하지 않습니다.

작성 시 COM3/115200/8N1/UTF-8, byte timeout 40ms에서 `ID?` + CRLF를 한 번 전송하여
`ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1` 전체 응답을 확인했습니다.
1000ms 캡처 ID는 `a4f9ae17d5244a5fbbd3ef2340831949`이며 gap/수신 누락/시리얼 오류는 0입니다.
두 시험 시퀀스의 실행 결과는 사용자가 Run을 눌러 별도로 확인합니다.
