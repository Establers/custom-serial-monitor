# COM3 실기 검증 결과 — 2026-10-31 (한국 시간)

**새 펌웨어 기록·읽기 검증 완료. 기본 30개 + 추가 15개 = 45개 실기 검사 통과.**
별도로 저장한 CLI 시퀀스 2개, 합계 8단계도 전부 `matched`였습니다.

## 타깃과 펌웨어

| 항목 | 확인 결과 |
| --- | --- |
| 보드 / MCU | NUCLEO-F103RB, STM32F103RB, device ID 0x410, Flash 128KB |
| ST-LINK | 066FFF485753667187145434 |
| 실제 포트 | ST-LINK Virtual COM Port, COM3 |
| 기록 주소 / 크기 | 0x08000000 / 9,836바이트 |
| BIN SHA-256 | `8AB5587A5FF6016724B35ED36F21CE402D2C077EBAE36AEA9E62D9DE69FDC986` |
| 다운로드 결과 | `Download verified successfully`, 타깃 MCU reset 완료 |
| 기존 Flash 백업 | `backups/066FFF485753667187145434-20261031-015302-576.bin` (131,072바이트) |
| 백업 SHA-256 | `B3ABCBD12725E65E99BD3135CB02A334F5FF7E19B700FA922369A1FC6408B1A8` |

교체 전 Hot Plug로 읽은 백업과, 기록 스크립트가 다시 읽은 백업의 해시가 일치했습니다.
옵션 바이트나 ST-LINK 자체 펌웨어는 변경하지 않았습니다.

## 실제 UART 검사

| 묶음 | 결과 / 범위 |
| --- | --- |
| 기본 통신 | ID, CR/LF/CRLF, 공백 보존, 여러 명령을 한 번에 전송: PASS |
| 문자 / 표시 fixture | 한글·일본어·emoji UTF-8, ANSI 원본 바이트, 혼합 줄 끝, INFO/WARN/ERROR: PASS |
| 긴 입력 / 출력 | 2,048바이트 줄, 2,000바이트 초과 명령 거부 후 정상 명령 복구: PASS |
| 타이밍 | 묵응답, 1.5초 지연 전 무응답과 이후 응답, UTF-8 바이트별 분할, STOP 취소: PASS |
| 텍스트 무결성 | BURST 1,000줄의 순번·개수·32바이트 padding 전체 비교: PASS |
| 병행 동작 | 유한 주기 로그, 주기 로그 중 PING: PASS |
| 오류 주입 | 매 5번째 누락 / 매 3번째 중복이 예상 순서에만 발생: PASS |
| 바이너리 | 모든 0x00..0xFF echo, 여러 프레임 결합 수신, 잘못된 CRC와 과긴 LEN 거부: PASS |
| HEX 무결성 | 1,000프레임의 길이·순번·payload·CRC 전체 비교: PASS |
| CRC 주입 | 21프레임 중 7/14/21번째만 CRC 오류: PASS |
| 누적 스트림 | 10초 × 6회, 총 6,000개 번호 로그 검증: PASS |
| 실제 baud 변경 | 9600, 19200, 38400, 57600, 230400, 460800, 921600, 115200: 모두 PASS |
| HEX 그룹 설정 | byte timeout 1/40/120ms 각각에서 20프레임 원본 무결성: PASS |
| 보드 명령 | LED ON/OFF 응답과 USER 버튼 상태 조회 응답: PASS |
| 재부팅 | RESET CONFIRM ACK → BOOT → PING 정상 응답: PASS |
| 카운터 | baud/mode 변경 후, 재부팅 후 rx_drops=0 / uart_errors=0 / tx_rejected=0: PASS |

누적 스트림 검사는 10초 유한 job 사이에 수집 여유가 있는 방식입니다.
며칠 동안 중단 없이 연결하는 내구 시험으로 해석하지 않습니다.
LED의 실제 빛과 버튼을 손으로 누르는 동작을 관찰한 검사는 포함하지 않았습니다.

## 앱과 재사용 시퀀스

기본 검사는 설치된 Serial Monitor **1.5.0**에서 실행했습니다.
실시간 baud/byte timeout 변경은 1.5.0에서 `unknown_operation`으로 확인되어,
프로젝트의 **1.6.1** 실행본으로 추가 검사를 진행했습니다.
설치 파일을 덮어쓰거나 다른 프로그램을 설치하지 않았습니다.

현재 COM3를 연결한 실행본:

```text
C:\Users\pjh\Desktop\serial\artifacts\sequence-review-fixes\SerialMonitor.WinUI.exe
instance-55888-735dac3e44014cf7b00e50a3f3ac4d9e
```

마지막 상태: connected=true, busy=false, Terminal, COM3, 115200, 8N1,
handshake=None, UTF-8, byte timeout=40ms, serialErrors=0.
마지막 `PING ready`의 실제 RX는 `PONG ready\r\n`입니다.
연속 생성 job과 의도적 오류 주입은 꺼진 상태입니다.

공유 프로필에 새 이름으로 저장했으며 기존 시퀀스는 덮어쓰지 않았습니다.

| 이름 | 실제 실행 결과 |
| --- | --- |
| `F103RB LAB - CLI smoke` | 7/7단계 matched, raw RX 4,168바이트 |
| `F103RB LAB - Binary CRC echo` | 1/1단계 matched, raw RX 21바이트 전체 프레임 비교 |

GUI v3 예제 5개는 현재 소스의 실제 JSON importer로 검증했습니다.
위 기본·추가 검사에는 화면에서 불러오고 GUI Run을 누르는 전체 동작을 포함하지 않았습니다.
별도로 수행한 GUI RX Check 실기는 아래에 기록합니다.

## GUI RX Check 추가 실기 — 2026-10-31 09:43 (한국 시간)

**GUI에서 새 v3 시퀀스를 불러오고 Run을 한 번 실행해 완료했습니다.**
시퀀스는 `F103RB LAB - GUI RX Check hardware validation`이며,
[가져오기 파일](sequences/hardware-validation/06-rxcheck-hardware.gui.json)을 공유 프로필에 새 이름으로 등록했습니다.
기존 시퀀스는 덮어쓰지 않았습니다. 1회 반복, TX 7개와 RX Check 8개, 총 15단계입니다.

| 항목 | 실제 결과 |
| --- | --- |
| 포트·설정 | COM3, 115200, 8N1, handshake=None, UTF-8, byte timeout=40ms |
| 펌웨어 | BUILD version=1.2.0, id=D4089FC9E87FBE0C |
| 실행 시각 | 09:43:49.680 ~ 09:43:54.925, 약 5.25초 |
| GUI run ID | `58696b927e2745a09ab444a633c63c0a` |
| 실행 결과 | completed, TX 7/7 sent, RX 7 matched / 1 expected not_matched, inconclusive=0 |
| 수신 증거 | 원본 291바이트 전체 확인, timestamp index에 누락·중복 없는 연속 범위 |
| 최종 상태 | connected=true, busy=false, host serialErrors=0 |

검사한 항목과 실제 증거:

- 즉시 PING: 응답 5.37ms. RX Check 활성화 전인 TX의 250ms 대기 중 도착한 응답이 정상 일치했습니다.
- UTF-8 바이트 분할: `SPLIT 10 준비 완료 RXC_SPLIT_20261031`의 전체 응답이 실제 RX chunk 34개에 걸쳐 도착했고 일치했습니다.
- 지연 응답: `DELAY 350 RXC_DELAY_20261031`의 응답이 354.30ms에 도착해 900ms 확인 창 안에서 일치했습니다.
- HEX: 완전한 21바이트 binary echo와 CRC가 일치했습니다. 별도 Python 표준 라이브러리로 payload·길이·순번·CRC-16/CCITT-FALSE도 확인했습니다.
- HEX `??`: 별도 RX Check가 앞선 TX의 HEX 모드를 상속하고 같은 전체 응답에서 정상 일치했습니다.
- 의도한 묵응답: `SILENT` 뒤 400ms 확인 창의 RX는 0바이트, 11단계는 예상대로 `not_matched`였습니다. 재송신 없이 다음 PING이 정상 일치했습니다.
- 모든 RX Check가 조기 일치 뒤에도 설정한 확인 창 전체를 기다렸습니다. 각 단계 attempt=1이며 RX 패턴은 송신되지 않았습니다.
- 장치 commands는 132 → 139로 정확히 7 증가했습니다. rx_drops=0, uart_errors=0, tx_rejected=0이며 기존 parse_errors=4는 증가하지 않았습니다.

GUI 창 식별 오류는 기본 이름의 설치본·개발 빌드가 창 제어 도구에서 설치 경로로 매핑되는 상황에서 재현됐습니다.
기존 프로젝트의 `UpdatePreviewBuild=true`로 생성된 별도 앱 이름 `SerialMonitor.UpdatePreview.exe`에서는
정상 창 선택·활성화·JSON 가져오기·Run·결과 창 조회가 모두 동작했습니다. 이 실행본을 사용해 매핑 충돌을 우회했습니다.
제어 도구 자체의 내부 코드는 변경하지 않았습니다.

실제 사용한 실행 파일은 `.codex-build/com3-full-20261031/gui-build/bin/SerialMonitor.WinUI/debug_win10-x64/SerialMonitor.UpdatePreview.exe`입니다.
메인 창과 결과 창 모두 실제 도구가 반환한 window ID로 선택했습니다.

증거는 저장소 루트의 `artifacts/rxcheck-hardware-20261031/`에 보존했습니다:
`verification-report.json`, `verify_evidence.py`, `gui-results.png`, `gui-results.ax.txt`,
`identify-result.json`, `final-status.json`, `sequence.gui.json`,
`run-58696b927e2745a09ab444a633c63c0a/`의 definition·summary·steps·원본 RX·timestamp index.
독립 증거 검사 41개가 통과했습니다. GUI의 미확인 1건은 의도한 음성 검사 결과이므로 장치 실패로 집계하지 않습니다.

## 보존한 증거

모든 경로는 이 폴더 기준입니다. `results/`와 `backups/`는 Git 추적에서 제외됩니다.

| 파일 / 폴더 | 내용 |
| --- | --- |
| `results/flash-20261031-015301.txt` | 모델/용량 확인, 백업, 기록, 읽기 검증, reset 원문 |
| `results/20261031-015324-570/report.json` | 기본 30개 PASS, 42개 capture, 319,679바이트 RX |
| `results/advanced-20261031-015635-500/report.json` | 추가 15개 PASS, 37개 capture, 3,518바이트 RX |
| 위 두 폴더의 `rx-*.bin`, `cli-*.json` | 전체 raw RX와 CLI/수집 메타데이터 |
| `results/smoke-result.json`, `smoke-rx.bin` | 7단계 시퀀스 결과와 전체 원본 |
| `results/binary-result.json`, `binary-rx.bin` | 바이너리 시퀀스 결과와 전체 원본 |
| `results/final-status.json` | 종료 시점 연결 설정 |

주요 capture ID:

- CLI smoke: `435238deaa744a42a90b991c9a5bb9a8`
- Binary CRC echo: `2b7ccdfbc11848eebf8abbfd165c4ff5`
- 마지막 PING: `308b00c850494e2bafa64474f22632c2`

PC에서 lab.c를 직접 실행한 52개 프로토콜 검사와, PC CRC/순번 검증기의 정상·오류 판별,
5개 GUI + 2개 CLI 정의의 실제 앱 파서 검사도 통과했습니다.

## 이번 결과에 포함하지 않은 범위

파일 로그 회전·UI 보관 한도·검색/필터/하이라이트의 화면 동작, 실제 USB 분리/재연결,
수일간 내구 시험, 실제 전기적 잡음과 RS-485/RTS/CTS 배선은 별도 검사입니다.
이번 무결성 판정은 GUI의 raw RX capture 경로를 통한 통신 결과입니다.
일반 파일 로그 기록 경로의 완전성을 대신 판정하지 않습니다.

빌드·재기록·재실행은 [README.md](README.md)의 절차를 사용하세요.
