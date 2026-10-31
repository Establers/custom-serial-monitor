# NUCLEO-F103RB USER 버튼 + 1초 로그 온보딩

2026-10-31: 펌웨어 1.3.0 빌드, 기존 Flash 백업, 업로드와 읽기 검증, COM3 실기 확인을 완료했습니다.
현재 보드는 **MONITOR ON**으로 계속 출력합니다.

## 바로 사용하기

1. 보드의 ST-LINK 쪽 USB 케이블을 연결합니다. 현재 포트는 **COM3**입니다.
2. Serial Monitor를 **115200 / 8N1 / 흐름 제어 None / UTF-8 / Terminal**로 연결합니다.
3. 별도 명령 없이 **1초마다 HEARTBEAT**가 나옵니다. `seq`와 `uptime_ms`가 증가하면 실행 중입니다.
4. 파란 **USER(B1)**를 위에서 누르고 뗍니다. `USER PRESS`, `USER RELEASE`와 `held_ms`가 나옵니다.
5. 명령을 보낼 때는 **text / CRLF**를 사용합니다. 로그를 잠시 멈췄다면 `MONITOR ON`으로 재개합니다.

USER는 PC13에 연결된 하나의 누름 버튼입니다. 상하좌우 방향은 구분하지 않습니다.
검은 RESET(B2)을 누르거나 전원을 다시 연결하면 카운터가 초기화되고 1초 출력이 다시 켜집니다.
연결 전에 출력된 BOOT 줄을 놓쳤더라도 HEARTBEAT가 계속 나오면 정상입니다.

출력 형식 예시:

```text
INFO HEARTBEAT seq=1 uptime_ms=1000 user=RELEASED presses=0 releases=0 rx_drops=0 uart_errors=0 missed=0
INFO USER PRESS count=1 uptime_ms=2055
INFO USER RELEASE count=1 held_ms=565 uptime_ms=2620
```

`held_ms`는 누른 시간(ms), `presses`/`releases`는 누름/뗌 횟수입니다.
버튼의 접점 흔들림은 20ms 디바운스로 처리합니다.
`rx_drops`, `uart_errors`, `missed`는 정상적인 저속 테스트에서 모두 0이어야 합니다.
TX 큐가 꽉 차면 배경 로그를 무한 재시도하지 않고 `missed`로 표시하며 명령 응답 공간을 남깁니다.

## 자주 쓰는 명령

| 명령 | 동작 |
| --- | --- |
| `MONITOR ON` | 버튼 이벤트와 1초 로그 켜기 |
| `MONITOR OFF` | 배경 출력 끄기. 버튼 상태·횟수는 내부에서 계속 갱신 |
| `MONITOR?` | 출력 설정, 디바운스 후 버튼 상태, 누름/뗌 횟수 조회 |
| `BUTTON?` | 현재 PC13 상태: 1=눌림, 0=뗌 |
| `STOP` | 모니터와 기존 스트림·burst·예약 출력 중단 |
| `BUILD?` | 버전과 빌드 ID 확인 |
| `PING hello` | `PONG hello` 응답 확인 |

정확한 바이너리 응답이나 무응답을 검사할 때는 먼저 `STOP` 또는 `MONITOR OFF`를 보냅니다.
검사를 마치고 `MONITOR ON`을 보내면 됩니다. 이 명령들은 화면을 Clear하지 않습니다.

앱의 Sequences에는 아래 3개를 플러그인 MCP로 저장했습니다. repeatCount=1, text/CRLF입니다.
저장 뒤 다시 조회해 정의가 유지되는 것도 확인했습니다.

- `F103RB USER - Monitor ON`
- `F103RB USER - Monitor OFF`
- `F103RB USER - Button status`

## 수정하고 다시 빌드·업로드하기

현재 설치된 STM32CubeIDE 1.17.0의 ARM GCC 12.3.1과 STM32CubeF1 1.8.6을 자동 탐색합니다.
CubeIDE를 열거나 CubeMX 프로젝트를 새로 만들 필요는 없습니다. CMSIS + C 기반 프로젝트입니다.

```powershell
cd C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab

# 빌드 + PC 회귀 검사. 보드를 변경하지 않습니다.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1 -BuildDirectory build-user-monitor

# 기존 Flash 전체 백업 후 이 보드에 업로드하고 읽기 검증합니다.
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\flash.ps1 -BuildDirectory build-user-monitor -SerialNumber 066FFF485753667187145434 -ReplaceFirmware
```

- `src/lab.c`: 주기 출력, 버튼 디바운스/이벤트, 명령 처리. `HEARTBEAT_MS`와 `BUTTON_DEBOUNCE_MS`가 주기 상수입니다.
- `src/main.c`: PC13 버튼, USART2 PA2/PA3, UART 큐와 인터럽트.
- `tests/test_lab.c`: 시간 경계, 바운스, 누름 시간, 32비트 tick wrap, TX 혼잡 시 응답 보존 검사.
- `build-user-monitor/serial-lab.bin`: 이번 업로드 파일.
- `build-user-monitor/manifest.json`: 빌드 ID, 도구, 입력·BIN 해시.
- `build-user-monitor/onboarding.json`: 이번 기록·실기 결과를 이미지 해시와 연결한 영수증.

파란 버튼을 눌러도 출력이 없다면 `MONITOR?`의 `enabled`를 확인하고 `MONITOR ON`을 보냅니다.
포트가 바뀌었다면 장치 관리자에서 ST-LINK Virtual COM Port 번호를 확인합니다.

## 이번 보드의 확인 결과

| 항목 | 결과 |
| --- | --- |
| 보드 / probe | NUCLEO-F103RB / `066FFF485753667187145434` |
| 버전 / 빌드 ID | **1.3.0 / ACFA6988DE234AFE** |
| 이미지 크기 / 주소 | 12,736바이트 / `0x08000000` |
| BIN SHA-256 | `BF0E473CBBD912C68B7737977072A2DFB7F6CC69D8FD3EDF848AFE5DB11AC3CA` |
| PC 검증 | C 프로토콜 **139개**, 독립 contract **81개** 통과 |
| 업로드 | 원본 128KiB 백업 후 기록, `Download verified successfully` |
| 부팅 기본 출력 | MONITOR ON 명령 전부터 1초 로그 확인 |
| 주기 | 연속 25개 로그를 두 수집 창에서 확인. 순번 연속, 보드 timestamp 간격 1000ms |
| 물리 버튼 | 사용자 화면에서 PRESS/RELEASE 확인 후, 보드 조회로 **누름 5회 / 뗌 5회** 확인 |
| 기존 통신 | 모니터 OFF에서 `00..FF` 256바이트 CRC echo 전체 일치 |
| 종료 상태 | COM3 / 115200 / Terminal / byte timeout 40ms / busy=false / serialErrors=0 / MONITOR ON |

25초 수집 창 2개에서는 버튼 이벤트가 관측되지 않았습니다. 이후 사용자가 화면에서 정상 출력을
확인했고, `MONITOR?`의 실제 누적 카운터로 5회/5회를 확인한 결과입니다.
소프트웨어로 GPIO를 조작해 물리 버튼 누름으로 표시하지 않았습니다.

주요 증거:

- 백업: `backups/066FFF485753667187145434-20261031-121533-604.bin`
- 백업 SHA-256: `F443A85077165DB52D8149030FECFAC4BC40A69C98613AF42697872D1CD06F8F`
- 업로드 스크립트 기록: `results/flash-user-monitor-20261031-121532-517.txt`.
  Programmer의 native 출력은 채팅의 실행 결과에 있으며, transcript에는 타깃 확인·백업·완료 메시지가 남습니다.
- 실기 8개 검사 / 34회 MCP 호출: `results/user-monitor-live-20261031-121547-108/`
- 주기 수집과 프리셋 저장/재조회: `results/user-button-observation-20261031-121658-194/`, `results/user-button-observation-20261031-121842-678/`
- 물리 버튼 누적 횟수: `results/user-button-confirmation-20261031-122013-206/physical-button-counters.json`

장치 제어 경로는 공식 MCP C# SDK → 저장소 플러그인 manifest/런처 → CLI 1.6.2 MCP → 기존 GUI → COM3입니다.
검증 프로세스에서만 `SERIAL_MONITOR_CLI`로 `artifacts/serialctl/serialctl.exe`를 지정했습니다.
이 채팅에 Serial Monitor 네이티브 도구가 노출된 경로의 검증과는 구분합니다.
GUI 모드를 바꾸거나 Clear하지 않았습니다. Quick CMD/Rules API도 확장하지 않았습니다.

공식 하드웨어 참고: [ST UM1724](https://www.st.com/resource/en/user_manual/um1724-.pdf).
