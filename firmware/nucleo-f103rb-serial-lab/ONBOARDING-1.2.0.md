# 1.2.0 보드 온보딩 및 UART 실기 결과 — 2026-10-31

**기존 Flash 백업 → 새 펌웨어 기록 → 읽기 검증 → 리셋 → 실제 COM3 검사까지 완료했다.**
플러그인 MCP 경로의 36개 검사가 통과했으며 GUI 모드 전환·Clear 호출은 0회다.

## 기록한 이미지

| 항목 | 실제 결과 |
| --- | --- |
| 보드 / probe | NUCLEO-F103RB / 066FFF485753667187145434 |
| MCU | device ID 0x410, Flash 128KB |
| 버전 / build ID | 1.2.0 / `D4089FC9E87FBE0C` |
| 기록 파일 / 주소 / 크기 | build-candidate/serial-lab.bin / 0x08000000 / 11,568바이트 |
| BIN SHA-256 | `E17E629901839006FD7AEF477246E4E4C1F430253BE68953B5135B1C192C1247` |
| 읽기 검증 | STM32CubeProgrammer: Download verified successfully |
| 교체 직전 전체 Flash 백업 | backups/066FFF485753667187145434-20261031-024959-360.bin / 131,072바이트 |
| 백업 SHA-256 | `13329D2CCA0B73E7710B37A15249FBCC31045EA21E2BCA07D77F095C3CB3387E` |

빌드 ID·CAPS·SELFTEST를 실제 UART 응답에서 먼저 확인했다.
기록 도구의 읽기 검증뿐 아니라 보드가 새 빌드로 실행 중이라는 응답까지 확인한 결과다.

## 플러그인 MCP 실기

검사 경로:

```text
공식 MCP C# SDK
→ plugins/serial-monitor/mcp.json
→ 플러그인 Start-SerialMonitorMcp.ps1
→ CLI 1.6.2 MCP 서버
→ 기존 Serial Monitor GUI 연결
→ COM3의 NUCLEO-F103RB
```

자동 탐지가 설치된 CLI 1.5.0을 선택해 처음에는 플러그인 호환성 검사에서 중단됐다.
장치 명령이 전송되기 전의 실패다. 검증 프로세스에서만 `SERIAL_MONITOR_CLI`를
`C:\Users\pjh\Desktop\serial\artifacts\serialctl\serialctl.exe`로 지정해 1.6.2를 사용했다.
앱 설치 파일이나 전역 클라이언트 설정을 교체하지 않았다.
이는 저장소 플러그인의 실제 stdio 런처를 SDK로 호출한 경로에 대한 검증이다.

| 검사 | 실제 결과 |
| --- | --- |
| BUILD? / CAPS? / SELFTEST | 선택한 manifest와 일치, PASS |
| payload 0/1/255/256/257/511/512/513/1023/1024 | 모든 바이트·LEN 상위 바이트·순번·CRC 일치 |
| 1,024바이트 payload | 1,035바이트 단일 응답 전체 일치 |
| LEN 필드 중간 분할 | 두 MCP 송신으로 나눠도 1KB payload 정상 조립·응답 |
| 서로 다른 최대 프레임 2개 결합 송신 | 2,070바이트 응답의 순서·내용·CRC 일치 |
| 최대 프레임 CRC 손상 | 정확한 CRC 오류 응답 |
| LEN=1025 헤더 | 정확한 LEN 오류 응답 |
| 고정 데이터 9종 × text/hex 송신 | 18개 모두 실행 token·build·길이·CRC·payload·trailer 일치 |
| 최종 연결 설정 | 최초 mode/baud/byte timeout 유지 |

총 **36개 검사 / 73회 MCP 도구 호출**.
35개 완성 응답의 전체 **11,603바이트**를 MCP 페이지로 끝까지 읽었다.
분할 헤더의 첫 송신에 대한 별도 capture까지 포함하면 capture ID는 36개다.
모든 capture에서 serialErrors=0, droppedBytes=0, rxGapDetected=false였다.
잘못된 CRC/LEN에 따른 펌웨어 parse_errors 증가는 의도한 입력 오류이며 UART 손실과 구분한다.

최대 프레임의 capture 예: `5b69995512d542a683f12db82e81ca2e`.
각 송신의 기본 수집 창은 750ms이며, 미리보기 64바이트 이후도 raw HEX 페이지로 모두 확인했다.

## 종료 상태와 증거

```text
instanceId: instance-103248-8502ec4bd38e4575a9a2d48a694de90f
COM3 / 115200 / 8N1 / no handshake / UTF-8
mode=terminal / byteTimeoutMs=40
connected=true / busy=false / serialErrors=0
```

- `results/onboard-1.2.0-20261031-024958-758.txt`: 백업·기록·읽기 검증 원문.
- `results/onboard-live-1.2.0-20261031-025022-121/`: 전체 MCP 요청/응답·raw RX·report·최종 상태.
- `build-candidate/onboarding.json`: 이미지 해시, 백업, 실기 결과, 검증기 DLL 해시를 연결한 영수증.
- `build-candidate/validation.json`: 앞서 수행한 PC 검사 결과. 이 파일의 hardwareVerified=false는 PC 단계만 뜻한다.

온보딩 영수증의 hardwareVerified=true가 이번 빌드의 실기 통과를 나타낸다.
일반 파일 로그 회전·UI 검색/이벤트의 시각적 동작·물리 USB 분리·72시간 내구·제품별 10비트 필드 packing은
이번 검사에 포함하지 않았다. 해당 범위는 test-plan.json의 별도 항목으로 유지한다.
