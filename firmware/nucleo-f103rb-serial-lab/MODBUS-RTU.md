# STM32 Modbus RTU 분석기 시험 — 펌웨어 1.4.0

기존 NUCLEO-F103RB / USART2 / ST-LINK 가상 COM으로 실제 Modbus 바이트를 보낸다.
추가 RS-485 어댑터나 배선은 필요 없다. 설정은 **115200 / 8N1 / 흐름 제어 없음**이다.
이 기능은 분석기 검증용 송신기다. 완전한 Modbus 서버·센서 구현은 아니다.
기존 PING, 버튼, HEARTBEAT, SERIAL-LAB/1 바이너리 기능은 유지한다.

## 화면에서 직접 확인

1. 보드에 연결한 뒤 text / CRLF로 `STOP`, `FAULT CLEAR`를 보낸다.
2. **Terminal 표시를 유지한 채** Protocol → Modbus RTU를 선택한다. 필요하면 분석 목록과 통계를 초기화한다.
3. 아래 명령을 **text / CRLF**로 한 번 보낸다. Protocol 분석은 Terminal 표시에서도 원본 바이트로 수행된다.
4. 성공한 MODBUS 명령의 RX는 원본 바이너리뿐이다. ASCII ACK·종료 문자열은 붙지 않는다.
   명령 자체의 ASCII **TX**는 분석에서 미해석 조각으로 보일 수 있다. 아래 기대값은 **RX만** 뜻한다.

```text
MODBUS NORMAL
MODBUS CRC
MODBUS EXCEPTION
MODBUS PREFIX
MODBUS CORRUPT
```

| case | RX 패턴 | RX 분석 기대값 |
| --- | --- | --- |
| NORMAL | FC03 / 값 250, 100 / 9바이트 | 결과 1, CRC 정상 1 |
| CRC | NORMAL의 CRC 상위 바이트 1비트 손상 | 결과 1, CRC 오류 1; 값 확정 안 함 |
| EXCEPTION | FC83 / 예외 02 / 5바이트 | CRC 정상 1, 예외 1 |
| PREFIX | `010304000001853A00` | 9바이트 응답 1; 내부 8바이트 요청으로 오분할 안 함 |
| NESTED | FC08/0000 본문에 CRC 정상 FC03 바이트 포함 | 미해석 정상 CRC 1; 내부 요청을 만들어내지 않음 |
| CORRUPT | FC03 본문에 정상 FC03 바이트 포함, 바깥 CRC 손상 | CRC 오류 1; 정상 요청을 만들어내지 않음 |
| PAIR | 요청 8바이트 + NORMAL 응답 9바이트를 한 번에 출력 | 결과 2, CRC 정상 2 |
| TRUNCATED | NORMAL의 앞 3바이트만 출력 | 불완전 1, CRC 오류 0 |
| GAP | NORMAL의 앞 3바이트 → **80ms** → 나머지 6바이트 | 불완전 2, CRC 정상/오류 0 |
| MAX | 125개 레지스터 / **255바이트** FC03 응답 | 결과 1, CRC 정상 1 |

GAP은 RTU의 문자 간격을 일부러 위반하는 음성 시험이다. 유효 프레임의 USB read 분할을
만드는 기능이 아니다. PAIR 역시 표시/분석기의 합쳐진 입력 처리용 바이트 패턴이며,
실제 선로에서 두 RTU 프레임 사이의 규정된 무통신 간격을 검증하는 시험이 아니다.

```text
MODBUS <case> [count=1] [gap_ms=100]
MODBUS NORMAL 10005 0
MODBUS?
STOP
```

count는 1..1,000,000, gap은 0..60,000ms다. GAP의 내부 조각 간격 80ms는 별도로 고정된다.
10005회 시험은 90,045바이트이며 115200bps에서 약 8초 이상 필요하다. 수신 창은 여유 있게 11초 이상 둔다.
10,005건 / 보관 10,000 / 만료 5 / CRC 오류·분석 누락 0이 기대값이다.
`MODBUS?`의 sent는 TX 큐에 수락된 시험 단위 수이며 PC 수신·해석 성공의 증명은 아니다.

모니터·다른 출력 job·오류 주입이 켜져 있으면 MODBUS는 BUSY를 반환한다.
출력 중 다른 명령/모니터를 섞지 않는다. 중단은 STOP으로 한다. 큐가 차면 같은 조각을 재시도하되
무한 메모리를 만들거나 이미 수락된 조각을 중복 전송하지 않는다. 리셋 후에는 기존처럼 1초 로그가 켜진다.

## 빌드와 독립 PC 검사

펌웨어 폴더에서 실행한다. 첫 두 명령은 보드에 접근하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/check-build.ps1 -BuildDirectory build-modbus-rtu
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-modbus.ps1 -Mode Offline
```

native C가 내보낸 `modbus-*.bin`을 별도 PC CRC/기대 데이터와 비교한 뒤 실제 앱의 분석 코어로 검사한다.
검사기는 WinUI 프로젝트의 수신 서비스를 재사용하므로 앱 빌드에 필요한 Windows 개발 환경이 필요하다.
예상값은 MCU의 CRC 함수나 출력 파일에서 복사해 정답으로 삼지 않는다.

## 업로드와 실제 UART 검사

다른 세션이 이 보드를 사용 중이지 않은지 확인한다. 기록 스크립트는 기존 128KiB Flash를 백업하고
지정한 ST-LINK / MCU ID / Flash 용량 / 이미지 해시를 확인한 뒤 기록·읽기 검증한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/flash.ps1 -BuildDirectory build-modbus-rtu -SerialNumber 066FFF485753667187145434 -ReplaceFirmware
```

다음 검사기는 **COM3를 직접 소유하는 개발용 검사기**다. 같은 포트의 GUI 연결은 사용자가 먼저 닫는다.
다른 COM 포트의 GUI는 그대로 두어도 된다. 실행 시 해당 포트를 열 수 없으면 실패하며 다른 연결을 끊지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-modbus.ps1 -Mode Probe -Port COM3
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/verify-modbus.ps1 -Mode Live -Port COM3
```

Live는 ID와 manifest의 BUILD를 검증하고 STOP/FAULT CLEAR 후 각 시험을 한 번만 전송한다.
실패한 송신을 자동 재전송하지 않는다. 실제 `SerialService`의 raw observer에서 수신 시각·native idle
경계를 그대로 분석기에 넘기고, 원본 바이트와 길이·순서·CRC/예외/불완전/누락/만료 집계를 비교한다.
최대 1MiB/4096청크의 메모리 증거만 보관하며 파일은 수집을 마친 뒤 기록한다.
마지막에 MCU의 rx_drops/uart_errors/tx_rejected도 0인지 확인한다.

`results/modbus-날짜-시각/`에 전체 RX `.bin`, 수신 청크 시각/경계, `report.json`이 남는다.
GUI 화면/탭 동작·RS-485 전기 신호·며칠간 운전을 검사한 결과로 확대해서 해석하지 않는다.
실행 종료 시 COM은 해제되고 보드는 MONITOR OFF로 남아 바이너리 재시험 준비 상태가 된다.

**CRC 오류, MCU가 보내지 않은 프레임, PC 분석 큐 누락은 서로 다르다.** Modbus에는 일반 순번이 없어
선로에서 없어진 패킷 수를 임의로 계산할 수 없다. CRC/손상 시험에서 분석 누락이 0인지 확인하고,
분석 큐 거부 카운터는 Offline의 작은 바이트 예산 시험으로 별도 검사한다.

재사용 명령은 [CLI 시퀀스](sequences/modbus/01-modbus-cases.cli.json)를 등록하거나
[GUI RX Check 시퀀스](sequences/modbus/01-modbus-cases.gui.json)를 불러올 수 있다.
HEX 화면에서 수동 입력은 명령의 HEX 바이트가 필요하다. GUI 예제는 ASCII 명령과 CRLF를
HEX로 인코딩하여 전송하므로 바이너리 RX Check 모드와 일치한다. CLI의 text TX/hex RX는 표시 모드와 독립적이다.
이번 실기 결과와 이미지/백업 식별은 [검증 결과](MODBUS-RTU-VALIDATION.md)에 기록한다.
