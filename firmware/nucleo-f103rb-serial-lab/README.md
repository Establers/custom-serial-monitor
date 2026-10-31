# NUCLEO-F103RB Serial Lab — 처음 연결부터 테스트까지

NUCLEO-F103RB 한 장과 현재 연결한 USB 케이블로 Serial Monitor를 시험하는 펌웨어입니다.
기본 통신, 시퀀스 응답 검사, Terminal/HEX, UTF-8, 이벤트, 대량 수신, 지연·분할 응답,
CRC와 순번 검증, 의도적인 누락·중복·CRC 오류를 재현합니다.

## 신뢰성 검증용 1.2.0 후보 빌드

다른 개발에서도 재사용할 설계·테스트 절차는
[장치 테스트 개발 가이드](../../docs/device-test-development-guide.md)를 참고하세요.
전체 기능별 입력·기대값·검증 수준은 [test-plan.json](test-plan.json)에 연결했습니다.
새로운 기본 빌드/PC 검증 명령은 다음과 같습니다. 보드 기록이나 화면 Clear는 하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1
```

`build-candidate/`에 버전 1.2.0과 빌드 ID·입력 해시를 포함한 후보 이미지가 생성됩니다.
[이번 후보 빌드 검증 결과](CANDIDATE-VALIDATION.md)에서 빌드 ID·해시·실행 범위를 확인할 수 있습니다.
1.2.0은 실제 보드 기록·읽기 검증과 플러그인 MCP 실기 36개까지 통과했습니다.
[온보딩 결과](ONBOARDING-1.2.0.md)에 이미지·백업·전체 UART 증거·종료 상태를 연결했습니다.
이전 실기 보고서는 각 보고서의 BIN SHA-256에 대한 결과입니다. `validation.json`의 hardwareVerified=false는
PC 검사 단계만 뜻하며, 실제 기록·실기는 별도의 `onboarding.json`에 hardwareVerified=true로 기록했습니다.

추가 명령:

| 명령 | 용도 |
| --- | --- |
| `BUILD?` | 펌웨어 version/protocol/16자리 build ID. 선택한 manifest와 먼저 비교 |
| `CAPS?` | 지원 크기 상한과 고정 데이터 9종 조회 |
| `SELFTEST` | CRC 기준 벡터, 고정 데이터 생성, 내부 크기 상수 검사 |
| `RUN case token` | 실행 ID·빌드 ID·길이·CRC·END가 붙은 고정 데이터. token은 영문/숫자/_/- 1..32자 |

case는 TEXT / UTF8 / CP949 / BYTES / NEWLINES / EVENTS / JSON / ANSI / CONTROL입니다.
다른 job·예약 출력·fault가 활성화된 동안 RUN은 BUSY를 반환합니다.
실제 UART 검증에는 `tests/FixtureContractCheck`의 mcp 모드를 사용하면
GUI 화면 모드를 유지하면서 명령별 text/hex 송신과 전체 raw RX를 비교할 수 있습니다.
빌드·기록·검증 명령 전체는 개발 가이드에 있습니다.

바이너리 payload 한도는 **1,024바이트**, 헤더·CRC를 포함한 최대 프레임은 **1,035바이트**입니다.
0/1/255/256/257/511/512/513/1023/1024 길이와 1025 거부를 검사합니다.
검사용 LEN은 16비트 little-endian을 유지합니다. 10비트 값의 최댓값인 1023과,
1KiB 크기인 1024를 별도 경계로 취급하며 상위 길이 바이트가 잘리는지 확인합니다.
RUN BYTES의 256종 바이트 fixture는 계속 256바이트이고, 큰 단일 binary ECHO는 별도 검사입니다.

## 1. 이 PC에서 확인한 것

2026-10-31 확인:

| 항목 | 실제 확인 결과 |
| --- | --- |
| 보드 | ST-LINK 조회 결과 `NUCLEO-F103RB` |
| MCU | SWD 장치 ID `0x410`, Flash `128 KBytes` |
| ST-LINK 일련번호 | `066FFF485753667187145434` |
| 시리얼 | `STMicroelectronics STLink Virtual COM Port (COM3)` |
| 드라이브 | `H:` / `NOD_F103RB` — 재연결하면 드라이브 문자는 바뀔 수 있음 |
| 개발 도구 | `C:\ST\STM32CubeIDE_1.17.0\STM32CubeIDE` |
| F1 SDK | `C:\Users\pjh\STM32Cube\Repository\STM32Cube_FW_F1_V1.8.6` |
| ARM 컴파일러 | CubeIDE에 포함된 GCC 12.3.1 |
| 다운로드 도구 | CubeIDE에 포함된 STM32CubeProgrammer CLI 2.18.0 |

추가 설치나 USB-UART 어댑터가 필요 없습니다. COM3는 **로그와 명령 통신용**이고,
펌웨어 기록은 같은 USB에 포함된 **ST-LINK/SWD**를 사용합니다.
기본 보드 배선은 USART2의 PA2(TX)/PA3(RX)를 ST-LINK 가상 COM에 연결합니다.
USB를 뽑으면 COM3뿐 아니라 ST-LINK도 함께 사라집니다.

## 2. 빌드 — 보드를 변경하지 않는 단계

PowerShell에서 다음 두 줄을 실행합니다.

```powershell
Set-Location C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

컴파일러와 SDK 경로는 자동으로 찾습니다. CubeIDE를 열거나 CubeMX에서 프로젝트를
새로 만들지 않아도 됩니다. 결과는 다음과 같습니다.

| 파일 | 용도 |
| --- | --- |
| `build/serial-lab.bin` | ST-LINK 또는 보드 드라이브로 기록할 바이너리 |
| `build/serial-lab.hex` | 주소 정보를 포함한 HEX 파일 |
| `build/serial-lab.elf` | 디버그 심벌을 포함한 실행 파일 |
| `build/serial-lab.map` | 함수·데이터 메모리 사용량 |
| `build/manifest.json` | 타깃, 연결 설정, 빌드 도구, BIN의 SHA-256 |

이 프로젝트는 STM32CubeF1의 CMSIS 헤더·시작 코드와 ARM GCC를 사용하는 C 프로젝트입니다.
`.ioc`를 재생성하는 HAL 프로젝트는 아닙니다. 수정할 곳은 `src/lab.c`(명령/시나리오)와
`src/main.c`(보드/USART 드라이버)입니다. 수정 후 같은 명령으로 다시 빌드합니다.

다른 PC에서는 [STM32CubeIDE](https://www.st.com/en/development-tools/stm32cubeide.html)와
STM32CubeF1 패키지를 설치합니다. CubeIDE 1.17.0에서는
`Help > Manage embedded software packages`에서 STM32F1 패키지를 설치할 수 있습니다.
설치 위치가 다르면 아래 환경 변수를 지정합니다. 관리자 권한이나 전역 실행 정책 변경은 필요 없습니다.

```powershell
$env:STM32_CUBE_IDE = 'C:\ST\STM32CubeIDE_1.17.0\STM32CubeIDE'
$env:STM32_CUBE_F1 = 'C:\Users\pjh\STM32Cube\Repository\STM32Cube_FW_F1_V1.8.6'
```

## 3. 보드에 기록 — 기존 펌웨어를 교체하는 단계

이 단계부터 타깃 MCU를 리셋하고 기존 프로그램을 새 테스트 펌웨어로 교체합니다.
스크립트는 **명시한 ST-LINK 일련번호**로 연결한 뒤 장치 ID와 128KB 용량을 확인하고,
기존 Flash 전체 128KB를 `backups/`에 먼저 저장합니다. 백업에 실패하면 기록하지 않습니다.
그다음 BIN 기록 → 읽기 검증 → 리셋을 수행합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\flash.ps1 `
  -SerialNumber 066FFF485753667187145434 -ReplaceFirmware
```

`-ReplaceFirmware`를 빼면 장치에 접근하기 전에 중단합니다.
읽기 보호 해제, 옵션 바이트 변경, 전체 지우기, ST-LINK 자체 펌웨어 업데이트는 하지 않습니다.
백업은 MCU Flash 프로그램만 보존하며 SRAM의 실행 상태나 외부 메모리는 포함하지 않습니다.

간단한 대안은 `serial-lab.bin`을 `NOD_F103RB` 드라이브에 복사하는 것입니다.
이 방법도 기존 펌웨어를 교체하고 자동 백업은 하지 않으므로 위 스크립트를 권장합니다.
복사 뒤 `FAIL.TXT`가 생기면 내용을 확인합니다. 드라이브에 파일이 복사됐다는 사실만으로
실행 성공을 판단하지 말고, 아래 `ID?` 응답까지 확인합니다.

## 4. Serial Monitor에서 첫 통신

1. Serial Monitor를 실행합니다.
2. `COM3`, `115200`, 데이터 8비트, Parity None, Stop 1, Handshake None을 선택합니다.
3. RX는 `Terminal`, 인코딩은 `UTF-8`, TX 줄 끝은 `CRLF`로 설정하고 Connect 합니다.
4. `PING`을 전송하면 `PONG`, `ID?`는 아래 문자열을 반환해야 합니다.

```text
ID NUCLEO-F103RB STM32F103RB SERIAL-LAB/1
```

보드의 검은 RESET 버튼을 누르면 다음 부팅 로그가 한 번 나옵니다.

```text
BOOT NUCLEO-F103RB SERIAL-LAB/1 baud=115200 8N1
```

파란 USER 버튼은 `BUTTON?`로 읽고 초록 LD2는 `LED ON`/`LED OFF`로 확인합니다.
부팅 직후 자동 로그 송신은 꺼져 있습니다. 생성 중인 모든 출력을 멈추려면 `STOP`,
의도적 오류 설정까지 해제하려면 `FAULT CLEAR`도 전송합니다.

## 5. 준비한 GUI 시퀀스

시퀀스 탭의 JSON 불러오기에서 `sequences/*.gui.json`을 선택합니다.
각 파일은 한 시퀀스이며, 별도의 TX와 RX Check가 있는 **FormatVersion 3**입니다.
현재 저장소의 RX Check 지원 빌드가 필요합니다. 이전 배포판이 v3를 거부하면
CLI smoke 시퀀스 또는 수동 명령으로 먼저 확인합니다.

| 파일 | 검사 |
| --- | --- |
| `01-basic.gui.json` | 보드 식별, PING, 공백, UTF-8 fixture, 로그 레벨, LED, 버튼, 카운터 |
| `02-timing.gui.json` | 800ms 지연, 바이트 분할, 2KB 줄, 주기 로그 |
| `03-burst.gui.json` | 1,000줄 대량 로그, 마지막 순번과 장치 오류 카운터 |
| `04-binary.gui.json` | 정확한 HEX echo/CRC, 번호 있는 바이너리 프레임 |
| `05-expected-timeout.gui.json` | **첫 RX가 미일치하는 것이 정상**, 다음 PING으로 계속 진행 |
| `hardware-validation/06-rxcheck-hardware.gui.json` | 즉시·분할 UTF-8·지연·HEX/와일드카드·의도한 타임아웃과 복구. COM3 GUI 실기 완료, [결과](HARDWARE-RESULTS.md#gui-rx-check-추가-실기--2026-10-31-0943-한국-시간) |

GUI 패턴의 일치만으로 전체 무결성을 보장하지 않습니다. 예를 들어 1,000번째 로그를
찾았어도 중간 로그가 빠질 수 있습니다. 전체 순서·개수·CRC 판정은 다음 검증기를 사용합니다.
GUI 시퀀스를 불러오거나 저장하는 것만으로 장치 명령을 전송하지 않습니다.

## 6. 실제 COM3 자동 검증과 증거 저장

GUI가 COM3/115200에 연결된 상태에서 실행합니다. 이 도구는 COM 포트를 직접 열지 않고
`serialctl`로 GUI가 이미 소유한 연결을 사용합니다. GUI가 여러 개면 실제 COM3에 연결한
인스턴스의 ID를 선택합니다.

```powershell
$cli = 'C:\Users\pjh\Desktop\serial\artifacts\serialctl\serialctl.exe'
& $cli instances
# 위 결과에서 대상 instanceId를 그대로 입력합니다.
$instance = '실제-instanceId'
& $cli status --instance $instance
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 `
  -CliPath $cli -InstanceId $instance -Port COM3
```

검증기는 먼저 `ID?` 응답을 확인하고, 다른 펌웨어이면 후속 테스트를 중단합니다.
CR/LF/CRLF, 공백, UTF-8/emoji, ANSI, 혼합 줄 끝, 2KB 줄, 묵응답,
지연 응답, UTF-8 바이트 분할, STOP, 과긴 명령 복구, 1,000개 text/HEX,
로그 중 명령 처리, 의도적 누락·중복·CRC 오류, 모든 256종 바이트의 echo를 검사합니다.
실패한 명령을 자동 재전송하지 않습니다.

각 수집의 **전체 raw RX**와 메타데이터, TX 파일, CLI 응답, 최종 `report.json`을
`results/날짜-시각/`에 보존합니다. 짧은 CLI 미리보기만 보고 판정하지 않습니다.
CRC 오류 주입은 예상한 위치만 허용하며, 실제 UART 오류와 capture gap은 별도로 실패 처리합니다.
검증 도구 자체는 baud를 바꾸거나 펌웨어를 리셋하지 않습니다.

한 시간 반복 수신 검사는 다음처럼 실행합니다. Ctrl+C 또는 GUI의 AI 중지로 중단할 수 있습니다.
10초 이하 분량씩 순번을 검증하며 전체 RX를 하나의 메모리 배열로 누적하지 않습니다.
중단 뒤 송신이 남으면 수동으로 `STOP`을 전송합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 `
  -CliPath $cli -InstanceId $instance -Port COM3 -SoakSeconds 3600
```

CLI에 저장해 재사용할 작은 시퀀스도 있습니다. `.cli.json`과 `.gui.json`은 서로 다른 형식입니다.

연결 세션과 중지·재시작 경합은 연결된 GUI의 정확한 instance ID를 지정해 따로 검증할 수 있습니다.
다음 검증기는 펌웨어 ID를 확인하고 PING과 모든 256종 바이트 CRC echo만 사용합니다.
GUI 모드 교대, 시퀀스 3회 반복, 중지 직후 재시작, 동시 CLI 제어 거절, COM3 닫기/다시 열기를
4라운드 실행합니다. 시험용 시퀀스는 새 이름으로 만들고 종료 시 그 이름만 삭제합니다.
진행 중인 다른 작업의 `busy`는 송신 전 거절임이 확인된 경우에만 기다립니다.

```powershell
pwsh -NoProfile -File .\scripts\verify-session-races.ps1 -CliPath $cli -InstanceId $instance -Port COM3 -Rounds 4
```

`tests/SessionRaceCheck`는 공식 MCP SDK 클라이언트 2개와 CLI 프로세스를 동시에 사용해
같은 연결을 검사합니다. `probe`는 송신 없이 병렬 응답을 비교하고, `wire`는 혼합 시퀀스,
중지 직후 CLI/MCP의 동시 시작 경쟁, 연결 닫기·다시 열기를 반복합니다. `broken`은 알려진
PING을 한 번 전송하면서 시험용 CLI의 stdout 수신만 의도적으로 닫습니다. 원본 캡처에
PONG이 정확히 한 번 남는지와 오류 종료·stderr를 확인하며 명령을 재시도하지 않습니다.
실행마다 비어 있는 증거 디렉터리를 지정하세요. 실행 파일 해시, PID, 종료 코드, stdout,
stderr, 전체 원본 RX를 보존합니다. 기존 시험과 동시에 장치 검사를 실행하지 마세요.

```powershell
dotnet run --project .\tests\SessionRaceCheck -- $cli $instance '.\results\new-probe' probe 30
dotnet run --project .\tests\SessionRaceCheck -- $cli $instance '.\results\new-wire' wire 10
dotnet run --project .\tests\SessionRaceCheck -- $cli $instance '.\results\new-broken' broken 3
```

```powershell
& $cli sequence save --instance $instance --file .\sequences\smoke.cli.json
& $cli sequence run --instance $instance --port COM3 --name 'F103RB LAB - CLI smoke'
# 반환된 runId로 sequence status --run <runId> 확인
```

## 7. 명령 사전

대소문자를 구분하며 CR, LF, CRLF를 모두 받습니다. 명령 한 줄 최대 511바이트,
초과/제어 문자가 있는 줄은 줄 끝까지 버리고 ERR을 출력합니다. 정상 텍스트 응답은 CRLF입니다.

| 명령 | 동작 / 제한 |
| --- | --- |
| `HELP`, `ID?`, `STATUS?` | 도움말, 버전, tick/수신 오류/거부된 제어 응답/주입 오류 카운터 |
| `PING [token]` | `PONG [token]` |
| `ECHO text` | `ECHO text` 그대로 응답. 공백/UTF-8 보존 |
| `STREAM START interval [count]` | 1..60000ms 주기, 순번 1부터. count 생략=계속, 지정 시 1..1000000개 |
| `STREAM STOP` | 주기 로그만 중지 |
| `BURST count [padding]` | 1..1000000줄, 줄별 A..Z 반복 padding 0..128바이트 |
| `HEX count` | 1..1000000개 바이너리 프레임. 각 프레임 43바이트 |
| `STOP` | stream/burst/HEX/지연·분할·긴 줄 출력 취소. 이미 UART에 들어간 바이트는 순서대로 송신 |
| `DELAY ms text` | 1..60000ms 후 text + CRLF. 예약 출력은 한 번에 하나 |
| `SPLIT gap text` | 1..60000ms 간격으로 **바이트 하나씩** 송신. UTF-8 경계 시험 가능 |
| `LONG n` | 1..2048바이트 A..Z 반복 + CRLF |
| `UTF8`, `ANSI`, `NEWLINES` | 한글/일본어/emoji, ANSI 색상, CR/LF/CRLF fixture |
| `LEVELS` | INFO → WARN → ERROR → INFO 로그 |
| `SILENT` | 명령 응답 없음. 켜져 있는 background 로그는 계속 출력 |
| `FAULT DROP n` | 매 n번째 번호 기록 생략, n=2..1000000 |
| `FAULT DUP n` | 매 n번째 번호 기록을 인접하게 한 번 더 출력 |
| `FAULT CRC n` | HEX 생성 프레임의 매 n번째 CRC만 1비트 변경 |
| `FAULT CLEAR` | 주입 설정 해제. 누적 카운터는 유지 |
| `LED ON`, `LED OFF`, `BUTTON?` | LD2(PA5), USER 버튼(PC13, 눌림=1) |
| `BAUD rate` | 9600/19200/38400/57600/115200/230400/460800/921600. 아래 전환 절차 참고 |
| `RESET CONFIRM` | 명시적인 소프트웨어 리셋. `RESET`만으로는 실행하지 않음 |

DROP/DUP는 DATA, BURST, 생성 HEX에만 적용하며 ACK나 명령 응답을 변형하지 않습니다.
동시에 설정한 경우 DROP이 우선합니다. 연속 로그는 불변 주기의 정밀 신호원이 아니라
부하 시험용입니다. 전송이 밀리면 메모리에 계속 쌓지 않고 생성 속도를 낮춥니다.
서로 다른 출력 job을 동시에 켜면 출력 단위 사이에 데이터가 섞일 수 있으므로,
정확한 단일 응답 검사 전에는 `STOP`을 사용합니다.

### Baud 변경

`BAUD 230400`을 현재 baud에서 전송하면 `OK BAUD 230400 SWITCH_IN_MS=500`을
현재 속도로 송신합니다. 최소 500ms가 지나고 기존 TX가 완전히 끝난 뒤 속도를 바꾸고
새 속도로 `READY BAUD 230400`을 출력합니다. 느린 baud에서 밀린 로그가 있으면 더 늦어집니다.
ACK를 받은 뒤 PC의 baud를 변경하고 `PING`으로 확인합니다.
CLI `configure --baud`는 PC 속도만 바꾸므로 장치의 BAUD 명령과 함께 사용해야 합니다.
고속 성공 여부는 ST-LINK/드라이버/HSI 오차에 따라 실제로 검증해야 합니다.
복구는 보드 RESET 버튼 → GUI 115200입니다. 설정을 Flash에 저장하지 않으므로
리셋하면 baud, fault, job이 기본값으로 돌아옵니다.

### 바이너리 프로토콜

텍스트 모드 진입 명령 없이 완전한 프레임을 HEX로 전송합니다. 명령 사이의 빈 줄 상태에서
`A5 5A`를 인식하며 프레임에 CRLF를 붙이지 않습니다.

```text
A5 5A | TYPE:u8 | SEQ:u32 little-endian | LEN:u16 little-endian | PAYLOAD[LEN] | CRC:u16 little-endian
```

LEN=0..1024. CRC-16/CCITT-FALSE: poly=0x1021, init=0xFFFF, refin/refout=false, xorout=0.
계산 범위는 TYPE부터 PAYLOAD 끝까지이며 A5 5A는 제외합니다. `123456789`의 CRC는 0x29B1입니다.

| TYPE | 요청/응답 |
| --- | --- |
| 0x01 / 0x81 | binary PING, 요청 payload와 순번을 그대로 응답 |
| 0x02 / 0x82 | binary ECHO, 0x00..0xFF 포함 payload 그대로 응답 |
| 0x03 / 0x83 | payload 없는 ID 요청, ID payload 응답 |
| 0x90 | `HEX count`가 생성. 순번 1부터, LEN=32, payload[i]=(seq+i)&255 |
| 0xE0 | 오류 응답. payload는 `CRC`, `LEN`, `TYPE` |

미완성 binary frame은 다음 바이트가 500ms 넘게 늦게 오면 폐기합니다. byte timeout 표시 설정은
실제 프레임의 길이/CRC와 별개입니다. 앱을 HEX로 전환해도 ASCII 명령을 쓰려면
그 명령의 ASCII 바이트를 보내거나 CLI `--text`를 명시합니다.

## 8. 앱에서 별도로 확인할 항목

자동 검증기의 raw RX 통과는 화면/파일 기록이 모두 정상이라는 뜻은 아닙니다.

| 영역 | 재현 및 판정 |
| --- | --- |
| 파일 로그/회전 | LOG ON, 작은 회전 크기, BURST/STREAM 실행. 모든 회전 파일에서 순번 합계·연속성 확인 |
| bounded UI/메모리 | 장시간 STREAM, 작업 관리자 메모리와 UI 반응, 앱 Health/drop/error 확인 |
| 일시정지 | Pause 동안 수신·파일 기록 지속 확인, Resume 동작 확인 |
| 검색/필터/강조 | LEVELS 반복으로 WARN/ERROR 규칙·검색·필터·전후 문맥 확인 |
| Terminal/HEX 전환 | UTF8와 HEX fixture. 모드 전환 시 기존 화면을 지우는 앱 동작 고려 |
| 응답 창 경계 | DELAY 1500 응답에 500ms/2500ms 창 적용, 미일치/일치 비교 |
| 재연결 | USB를 직접 분리·연결해 COM 소실/재등장/자동 복구 확인 |
| 재부팅 | RESET CONFIRM 또는 검은 버튼. UART 부팅 로그와 Windows COM 재연결을 구분 |
| 프로필 | 포트/baud/encoding/규칙/시퀀스 저장 후 다시 불러오기 |

한 보드와 ST-LINK VCP만으로 RS-485 방향 전환, 실제 전기적 잡음, 케이블 단선,
RTS/CTS 배선, 모든 baud/parity 조합을 자동으로 검증하지는 못합니다.
CRC/DROP 주입은 프로토콜 오류를 재현하며 실제 전기적 오류와 구분합니다.
수 µs 단위 wire 타이밍도 USB/Windows 수신 시각만으로 보장하지 않습니다.

## 실제 보드 검증 결과와 추가 검사

플러그인 런처 경유 MCP의 Terminal/HEX 교차 송수신, 전체 RX 페이지, 시퀀스 실행도
추가 검증했습니다. **13개 검사 / 61회 MCP 호출 통과**이며,
[PLUGIN-MCP-RESULTS.md](PLUGIN-MCP-RESULTS.md)에 실제 호출 경로·증거·Codex 호스트 연결 범위를 기록했습니다.

이후 빠진 기능까지 확장한 **21개 도구 전체의 정상/거부 검사**도 완료했습니다.
최종 30개 검사 묶음과 238회 MCP 호출, SDK 취소 전파 관찰, 모드 초기화 영향은
[ALL-PLUGIN-FEATURES-RESULTS.md](ALL-PLUGIN-FEATURES-RESULTS.md)를 참고하세요.

2026-10-31에 위 펌웨어를 이 보드에 기록하고 실제 COM3에서 기본 30개 + 추가 15개,
총 45개 실기 검사를 통과했습니다. 별도 CLI smoke 7단계도 모두 일치했습니다.
자세한 범위와 증거 위치는 [HARDWARE-RESULTS.md](HARDWARE-RESULTS.md)를 참고하세요.

기본 검증은 설치된 Serial Monitor 1.5.0에서 수행했습니다. 실시간 설정 변경이 필요한
추가 검증은 `artifacts/sequence-review-fixes/SerialMonitor.WinUI.exe` 1.6.1에서 수행했습니다.
설치된 프로그램의 파일은 교체하지 않았습니다. 이후 검사에도 1.6.1 이상을 사용하세요.

```powershell
# 기본 검사와 별도로, 8종 baud/HEX 그룹 설정/소프트웨어 리셋 검사
# 주의: 장치 baud가 실제로 바뀌고 마지막에 소프트웨어 리셋을 실행합니다.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-advanced.ps1 `
  -CliPath $cli -InstanceId $instance -Port COM3 -AllowDisruptiveChecks
```

추가 검증은 9600, 19200, 38400, 57600, 230400, 460800, 921600, 115200 순서로
장치와 PC 속도를 함께 변경하며, 완료하면 115200 / Terminal / byte timeout 40ms로 복구합니다.
중간 실패로 속도가 불명확하면 자동으로 BAUD 명령을 재전송하지 않고 중단합니다.
검은 RESET 버튼을 누른 뒤 PC 속도를 115200으로 맞춰 복구하세요.

`sequences/binary.cli.json`은 제어 문자와 높은 바이트를 포함한 바이너리 CRC echo 한 단계를
CLI에 저장해 재사용하는 파일입니다. 실제 요청/응답의 전체 프레임을 포함합니다.

## 9. 개발자 검증

```powershell
# PC에서 실제 lab.c를 MSVC로 빌드해 프로토콜 테스트 — 보드 불필요
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test.ps1
# PC CRC/순번 판정기의 정상/오류 판별과 스크립트 구문 확인
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-codec.ps1
# 현재 앱의 실제 GUI JSON importer / CLI validator로 예제 형식 검증
dotnet run --project .\tests\AssetsCheck -- .\sequences
# 시퀀스 fixture 재생성
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\make-sequences.ps1
```

펌웨어 수신 ISR은 고정 크기 RX 큐에 넣고 송신 ISR은 TX 큐에서 꺼내기만 합니다.
명령 해석과 로그 생성은 main loop에서 처리하며, TX 큐에는 제어 응답용 1.5KB를 남깁니다.
동적 할당을 사용하지 않고, tick wrap, TX backpressure, 과긴 입력, CRC/길이 오류를 검사합니다.
링커는 4KB stack과 256바이트 libc heap 여유를 포함해 RAM 한도를 검사합니다.
생성물·장치 백업·수집 결과는 이 폴더의 `.gitignore`로 제외됩니다.

## 문제 해결

Quick CMD의 생성 검증·테스트 프로필 저장/재읽기·Terminal/HEX 플러그인 재생 결과와
재실행 방법은 [QUICK-CMD-RESULTS.md](QUICK-CMD-RESULTS.md)에 있습니다.
GUI의 생성 대화상자와 버튼 클릭은 창 소유자 검증 오류로 아직 미검증이며,
Quick CMD 자체는 개별 모드 대신 현재 전역 TX 모드를 사용합니다.

- **COM3 없음:** 보드 USB, 데이터 케이블, 장치 관리자에서 ST-LINK/포트 확인. 재연결 후 COM 번호가 바뀔 수 있습니다.
- **연결 거부:** 다른 터미널이나 CubeIDE Serial Console이 포트를 열었는지 확인합니다. 하나의 GUI 연결을 공유합니다.
- **ST-LINK는 있는데 SWD 연결 실패:** 보드 CN2 점퍼 두 개와 전원 확인. flash 스크립트는 Under Reset 연결을 사용합니다.
- **문자가 깨짐:** 보드 RESET 후 115200/8N1/None/UTF-8로 복구합니다.
- **PING 무응답:** firmware ID/기록 검증 확인, CRLF 확인, 보드의 USART2 solder bridge 기본 배선 확인. 무응답만으로 보드 고장으로 판정하지 않습니다.
- **도구 경로 못 찾음:** 2절의 환경 변수를 지정합니다.
- **검증 도중 실패:** `results/.../report.json`, `cli-*.json`, `rx-*.bin.json`의 sendStatus/captureStatus와 raw RX를 먼저 봅니다.

공식 참고: [보드 매뉴얼 UM1724 — ST-LINK/USART/점퍼](https://www.st.com/resource/en/user_manual/um1724-stm32-nucleo64-boards-mb1136-stmicroelectronics.pdf),
[STM32CubeIDE](https://www.st.com/en/development-tools/stm32cubeide.html),
[STM32CubeProgrammer](https://www.st.com/en/development-tools/stm32cubeprog.html).
