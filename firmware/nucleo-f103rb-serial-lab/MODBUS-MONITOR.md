# RTU 폴링·직접 쓰기·오류 주입 시험 — 1.7.0

기존 LAB 명령, Modbus 주소 0/1 및 0x0100 CRC 시험, BACnet MS/TP Test Request/Response를 유지한다.
부팅은 기존과 같은 **Unit 1 / 115200 / 8N1 / 흐름 제어 없음 / 조용한 RTU 모드**다.
추가 레지스터·코일은 RAM에만 저장하며 Flash에는 쓰지 않는다. MCU 리셋 또는 `RTU RESET`으로 초기화한다.

## 가장 먼저 해 볼 것

1. COM3에 연결하고 기존 **Protocol 탭 → Modbus RTU → 레지스터 모니터**를 선택한다.
2. 설정에서 ID `1`, FC04, 시작 주소 `0x1000`(십진수 4096), 수량 `6`, 주기 `500 ms`,
   타임아웃 `1000 ms`, 표시 `UInt32`, 배율 `1`, 바이트/워드 교환 꺼짐을 설정한다.
3. **한 번 읽기**로 확인한 다음 **폴링 시작**을 누른다. 첫 행은 계속 증가하는 밀리초 카운터다.
   다음 두 행은 쓰기 횟수와 정상 읽기 횟수다. 시작 주소는 0 기반 PDU 주소이며 40001 표기 번호가 아니다.
4. 중지하고 FC03, 주소 `0x1000`, 수량 `10`, 표시 `UInt16`으로 바꾼다.
   다시 **폴링 시작**을 누르고, **직접 쓰기…**에서 주소 `0x1000`, 값 `123 456`을 입력해 **쓰기 및 확인**을 누른다.
   FC10 한 번으로 반영하고 FC03 재조회로 확인한다. 값 하나면 FC06을 사용한다.
5. 중지하고 FC01, 주소 `0x1000`, 수량 `16`으로 바꾼다. 직접 쓰기 값 `1`은 LD2 켜기,
   `0`은 끄기다. 값 여러 개는 FC0F, 하나는 FC05다.
6. **설정 저장** 후 앱을 다시 열면 설정을 확인할 수 있다. 통신 시작은 직접 누른다.

직접 쓰기 값은 표시 배율·자료형과 관계없이 원시 UInt16 또는 코일 0/1이다.
폴링 소유 중에는 수동 TX·기존 시퀀스·자동화·브리지를 함께 실행할 수 없다.
기존 시퀀스를 시험하려면 먼저 폴링을 중지한다. 오류로 결과가 불확실하면 앱은 원래 연결을 닫으며 쓰기를 자동 재시도하지 않는다.

## 주소표

주소는 모두 0 기반, 숫자와 레지스터 내부 바이트는 big-endian이다. 32비트는 상위 워드가 먼저 온다.

| 함수/주소 | 크기 | 의미 / 초기값 |
| --- | --- | --- |
| FC03 `0x1000..0x107F` | 128 words | 읽기·쓰기 RAM; FC06/10으로 변경 |
| Holding `0x1000..0x1003` | 4 words | 0; 일반 쓰기 시험 영역 |
| Holding `0x1004` | 1 word | `0xFF85` → Int16 -123 |
| Holding `0x1005` | 1 word | 250 → 배율 0.1이면 25 |
| Holding `0x1006..0x1007` | 2 words | `0x1234 0x5678` → UInt32 305419896 |
| Holding `0x1008..0x1009` | 2 words | `0x41C8 0x0000` → Float32 25 |
| FC04 `0x1000..0x1001` | UInt32 | 시험 상태 초기화 후 경과 ms; 32비트 순환 |
| Input `0x1002..0x1003` | UInt32 | 반영된 쓰기 요청 횟수; 값이 같아도 요청당 한 번 |
| Input `0x1004..0x1005` | UInt32 | 펌웨어가 정상 처리한 읽기 횟수; 이번 읽기 포함 |
| Input `0x1006` / `0x1007` | 1 / 1 word | USER 버튼 눌림 / RTU LED 명령 상태 (0/1) |
| Input `0x1008..0x100D` | 6 words | -123, 250, UInt32 `0x12345678`, Float32 25의 고정 패턴 |
| Input `0x100E..0x107F` | 114 words | 예약, 0; FC04는 읽기 전용 |
| FC01/05/0F `0x1000..0x17FF` | 2048 bits | RAM 코일; 첫 비트가 LD2, 초기값 모두 0 |
| FC02 `0x1000..0x17FF` | 2048 bits | 첫 비트 USER 버튼, 둘째 RTU LED, 나머지 0 |

일반 읽기는 최대 125 registers / 2000 bits, 연속 쓰기는 최대 123 registers / 1968 bits다.
읽기 횟수는 PC의 수신 성공 횟수가 아니다. 이후 일회성 DROP/CRC로 응답을 잃어도 증가하며, 기존 0x0100 고의 CRC 창은 제외한다.
범위·길이·수량·CRC를 검증한 뒤 전체 쓰기를 한 번에 반영한다. 일부 주소만 반영하는 동작은 없다.
Unit 0 방송·다른 Unit·잘못된 요청 CRC에는 응답과 쓰기 부작용이 없다.
기존 FC03/04 주소 0=250, 1=100은 읽기 전용이며, 0x0100 수량 2의 고의 CRC 오류도 유지한다.
잘못된 주소는 예외 02, 잘못된 값·수량·길이는 예외 03, 미지원 함수는 예외 01이다.

## 한 번만 적용하는 오류

폴링을 멈춘 뒤 LAB 모드에서 준비한다. 명령은 ASCII + CRLF이며 Terminal/HEX 화면 모드와 장치 LAB/RTU 모드는 별개다.

```text
LAB
RTU RESET
RTU FAULT CRC
RTU
```

각 명령의 `OK` 응답을 받은 뒤 다음 명령으로 진행한다. 마지막 `OK RTU`까지 수신한 뒤 RTU 읽기를 시작한다.
오류는 **다음 Unit 1 Modbus 응답 한 개**에만 적용하며 예외 응답도 포함한다. 다른 Unit·잘못된 요청 CRC·MS/TP 요청은 소비하지 않는다.

| LAB 명령 | 동작 |
| --- | --- |
| `RTU STATUS?` | 읽기/쓰기 횟수, LED, 예약 오류, 지연 및 대기 응답 조회 |
| `RTU RESET` | RAM·카운터·LED·오류·대기 응답 초기화; MCU 리셋 아님; LAB에 머묾 |
| `RTU FAULT CLEAR` | 예약 오류와 대기 응답 취소 |
| `RTU FAULT DROP` | 응답 없음 |
| `RTU FAULT CRC` | 응답 CRC 손상 |
| `RTU FAULT TRUNCATE` | 첫 3바이트만 응답 |
| `RTU FAULT WRONGID` | 정상 CRC를 가진 Unit 2 응답 |
| `RTU FAULT DELAY 100` | 100ms 뒤 응답; 범위 1..5000ms |
| `RTU FAULT SPLIT 100` | 첫 3바이트 뒤 100ms 무음, 나머지 응답; 범위 1..5000ms |

쓰기 후 DROP은 **값과 쓰기 횟수는 반영되고 ACK만 사라지는** 상황이다. 재연결 후 횟수와 값을 읽어 중복 실행 여부를 확인한다.
오류를 예약한 뒤 bare `RTU`로 들어가면 예약을 유지한다. `LAB`/`STOP`은 대기 응답과 예약 오류를 취소한다.
`RTU STATUS?`의 `fault`와 `delay_ms`는 아직 소비하지 않은 예약을 뜻한다. 소비 후 전송 대기 중이면 `fault=NONE`, `pending=1`일 수 있다.
기존 `FAULT ...`, `MODBUS CRC/GAP`은 기존 LAB 송신기용이며 새 `RTU FAULT ...`와 독립적이다.
지연 응답은 비차단 단일 슬롯이며 처리한 쓰기를 TX 큐 재시도 때문에 다시 반영하지 않는다.
취소는 소프트웨어 대기 버퍼를 비우며 UART 큐로 이미 전달된 바이트를 되돌리지는 않는다. 복구 전에는 남은 RX를 먼저 수집한다.

## 자동 검사

펌웨어 폴더에서 실행한다. `check-build`는 ARM 빌드·네이티브 C·독립 fixture·시퀀스 파서·RTU/MS/TP 오프라인 검사를 수행하며 장치에 접근하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1 -BuildDirectory build-modbus-monitor
```

실제 검사는 **COM3를 사용하는 GUI 연결만 닫고**, 기록된 보드의 manifest와 같은 build 폴더를 지정한다.
다른 COM 장치나 GUI는 종료할 필요 없다. 다음 한 명령은 포트를 직접 열고 실제 SerialService/ModbusRtuMonitorService를 사용한다.
펌웨어를 자동 기록하지 않으며, BUILD가 다르면 변경 시험을 중단한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-modbus-monitor.ps1 -Mode Live -Port COM3 -BuildDirectory build-modbus-monitor -EvidenceDirectory ('.\results\monitor-' + (Get-Date -Format yyyyMMdd-HHmmss-fff))
```

전체 TX/RX, 수신 시각·세션, 사례별 판정과 `report.json`은 새 `results/` 하위 폴더에 남는다.
검사는 RAM·LED·오류를 변경하고 일부 오류 사례에서 연결을 닫았다 다시 연다. 정상 완료 시 오류 해제·시험 상태 초기화·LED OFF·조용한 RTU 상태로 돌린다.
실패·취소 시에는 포트만 닫고 증거를 저장한다. RAM·LED·오류 상태가 남을 수 있으므로 보고서를 확인한 뒤 명시적으로 LAB에서 초기화한다.
이전 LAB/분석기/최대 MS/TP 응답의 추가 회귀 검사는 `verify-modbus.ps1`, `verify-rtu-server.ps1`, `verify-mstp.ps1`의 Live 모드를 사용한다.

GUI에서 한 번 실행하려면 [03-monitor-extended.gui.json](sequences/modbus/03-monitor-extended.gui.json)을
**시퀀스 관리 → JSON 불러오기**로 가져온다. 폴링·브리지·다른 시퀀스를 중지하고 **자동 재연결을 끈 뒤** HEX 모드에서 실행한다.
가져오기 파일은 전역 재연결 정책을 바꾸지 않는다. 자동 재연결이 켜져 있으면 GUI는 연결 실패 TX를 재시도할 수 있다.
목록에서 **STM32 Modbus Monitor - 확장 레지스터·쓰기·LED·오류 복구**를 선택하고 **Run**을 누른다.
총 52단계(TX 26 / RX Check 26), 각 수신 창 700ms라 대기 합계 18.2초이며 저장·처리 시간을 포함해 약 19초가 걸린다.
정상 기대값은 RX Check 26개 전부 일치다. 고의 CRC 오류 단계도 지정한 손상 바이트를 받으면 일치이고,
Protocol 패킷 분석에서는 CRC 오류로 보여야 한다. FC02 단계의 버튼·CRC는 `??` 와일드카드라 GUI 패턴 검사만으로는 무결성을 판정하지 않는다.
이 파일은 GUI v3 TX/RX Check 전용이며 `serialctl sequence save` 입력 형식과 다르다.
변하는 카운터의 값·시간 관계, 전체 CRC, 소유권 충돌, 재시도 여부는 위 자동 검사기로 판정한다.
Live 검사는 같은 가져오기 파일을 실제 시퀀스 실행기와 코어 RX Check 경로로 두 번 실행하고,
별도 독립 oracle이 FC02의 전체 CRC·사용하지 않는 비트·LED 관계도 확인한다. GUI 컨트롤은 띄우지 않는다.
GUI 패턴 일치와 실제 마스터 서비스 검사, 화면을 직접 실행한 검증은 서로 다른 범위다.
실제 업로드 결과는 [MODBUS-MONITOR-VALIDATION.md](MODBUS-MONITOR-VALIDATION.md)에 기록한다.

USART2/ST-LINK VCP 기반 시험이다. 5ms 조립 무음과 1ms tick을 사용하므로 RS-485 전기 특성·DE 전환·RTU 정밀 타이밍 적합성을 보증하지 않는다.
