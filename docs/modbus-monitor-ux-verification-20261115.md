# 장치 감시·제어 UX 검증 — 2026-11-15

## 판단과 변경

장치의 값을 요청해서 읽고, 반복 조회와 검증된 쓰기를 제공하는 기능 방향은 유지한다.
혼란의 주원인은 내부 용어(대상·활성 그룹·폴링), 한 번 읽기의 불명확한 범위,
아직 요청하지 않은 행의 응답 대기 표시, 기본 설정과 표시 옵션의 혼재였다.
통신 서비스와 UI의 분리, 연결 소유권, 순차 쓰기·재조회는 유지했다.

- 기본 흐름: 읽기 설정 → 현재 값 읽기 → 자동 읽기. 설정 창에서 바로 읽을 수 있다.
- ID·FC·시작–끝 주소·개수를 표시한다. 그룹 읽기와 전체 포함 그룹 자동 읽기를 구분한다.
- 선택 그룹의 자동 읽기 포함 여부를 설정에서 바꿀 수 있다. 변경 자체는 TX하지 않는다.
- 기본 설정을 먼저 보이고 표시 형식·배율·바이트 순서는 접었다. 표는 이름과 값을 앞에 둔다.
- 읽기 전·읽는 중·응답 실패·연결 끊김·중지·과거 값을 구분하고, 주요 통신 오류에 확인할 항목을 안내한다.
- 짧은 창에서 초기 안내가 표 제목과 겹치던 문제를 수정했다.
- 공통 디자인과 문구 기준을 [UI 기준](modbus-monitor-ux.md) 및 AGENTS.md에 연결했다.

시작 기준은 main `ee29382`, 작업 트리와 index는 깨끗했다.
관련 기존 채팅에 전달한 두 수정은 이 작업 중 main에 먼저 반영됐다.

- `007e1bc`: Modbus 펼침 메뉴의 크기·여백·화살표를 공통 스타일로 통일.
- `7b11572`: 그룹 이동·새 그룹에서 유효한 편집 초안을 보존하고, 잘못된 입력은 해당 그룹에 남겨 수정하도록 처리.

## 자동 검증

| 검사 | 결과 | 근거 |
| --- | --- | --- |
| Release 솔루션 빌드 | 경고 0, 오류 0 | `artifacts/modbus-ux-build.log` |
| WinUI Modbus/Protocol 회귀 | 597 통과, 1 환경 의존 검사 건너뜀, 실패 0 | `artifacts/modbus-ux-tests/winui-ux-final.trx` |
| Core Modbus 회귀 | 164 통과, 실패 0 | `artifacts/modbus-ux-tests/core-ux.trx` |
| 실제 WinUI 레이아웃, 1280×900 / 800×600 | 86 사례·150 공통 폼 그리드 통과 | `artifacts/modbus-ux-layout-latest/ef42141c9f424b658ba031cd0f22843b/result.json` |
| 펌웨어 빌드·독립 계약 | C 검사 5831개·독립 계약 81개 통과 | `artifacts/firmware-ux-build-20261115.log` |
| 실제 UART·모니터 서비스 | 24/24 통과, serialErrors 없음, 증거 overflow 없음 | `artifacts/modbus-ux-hardware-20261115/report.json`, `wire.jsonl` |

건너뛴 WinUI 검사는 가상 COM 드라이버를 요구하는
`AutomationVirtualWireIntegrityTests.CliAndMcpCarryExactModbusBinaryAndTerminalBytesThroughTheWindowsDriver`다.
전체 솔루션 테스트는 Core 실행 대기 중 중단했으며 전체 통과로 간주하지 않는다.
그 뒤 실제 빌드 출력(`bin/x64/Release` WinUI, `bin/Release` Core)에서 관련 검사를 실행했다.
새 회귀 검사는 그룹 포함 변경의 무송신·저장·실행 중 변경 차단, 읽기 범위, 행 상태와 오류 안내를 확인한다.

## 펌웨어 기록

- 장비: NUCLEO-F103RB / ST-LINK `066FFF485753667187145434`, MCU ID 0x410, Flash 128 KiB.
- 기존과 동일한 FW 1.7.1 / BUILD `22F524185C55784C`를 소스에서 재빌드해 기록했다. 버전 추가 개발은 하지 않았다.
- BIN SHA256: `7FD57B2CEE1A887B74239030669A67A78188DFD56F85BBDE206D06F5F8A75980`.
- 기록 전 전체 Flash 백업: `firmware/nucleo-f103rb-serial-lab/backups/066FFF485753667187145434-20261115-101008-802.bin`.
- 백업 SHA256: `74B1169AF379B8404EF1E602B1DEA34D88BEA949CFCB5FB77C8A5C85B7BA2CDC`.
- STM32CubeProgrammer 기록·검증·리셋 성공: `artifacts/firmware-ux-flash-20261115.log`.
- ST-LINK 자체 펌웨어를 바꾼 작업은 아니다.

실기 설정은 COM3 / 115200 / 8N1 / handshake None / HEX idle 40 ms다.
기존 두 GUI가 연결 해제된 상태에서 실제 SerialService/ModbusRtuMonitorService로 검사했다.
읽기·쓰기·자료형·코일·오류 주입·연결 소유권·폴링 중 쓰기와 GUI 시퀀스 파일의 실제 실행기를 검사했다.
시퀀스 파일 재생은 화면 클릭 검증과 구분한다. 실기 자동 검사 종료 시 RAM·오류 상태를 초기화하고 조용한 RTU 상태로 복귀했다.

## 실제 화면 조작

Computer Use로 별도 Debug UpdatePreview 빌드를 조작했다. 기존 사용자의 두 창은 종료하지 않았다.

1. 초기 읽기 설정, 주소 범위와 버튼 안내를 확인했다.
2. FC03 / 주소 0 / 10개는 장비가 예외 02로 거절했다. 이를 주소·개수 확인 안내로 바꿨다.
3. 개수를 2로 바꿔 표에서 250 / 100과 정상 RX를 확인했다.
4. 자동 읽기·중지 버튼을 눌러 반복 수신, 중지 뒤 마지막 값 유지와 과거 값 표시를 확인했다.
5. RAM 주소 4096 / 2개를 읽고 행 선택 → 값 쓰기로 1234를 기록했다. ACK와 재조회, 표의 1234를 확인했다.
6. RAM 원래 값 0을 복구했다. 복구 ACK 캡처 `acd27cd505f04d4f908e7f3a8076b8b3`의 RX는 `01 06 10 00 00 00 8D 0A`다. 이후 GUI 재조회에서도 0 / 0을 확인했다.
7. 최종 소스를 다시 빌드해 초기 안내와 표 제목의 겹침 해소, 설정 창의 문구·배치를 확인했다. 실제 버튼으로 RAM 0 / 0을 다시 읽었다.

최종 확인용 실행 파일:
`artifacts/modbus-ux-cua-final-20261115/SerialMonitor.UpdatePreview.exe`.
최종 인스턴스는 `instance-18904-c84af039d6424e8d800541da2f7c2f48`, COM3 연결 유지,
busy=false, serialErrors=0, ID1 / FC03 / 주소4096–4097 / 2개다.
근거: `artifacts/modbus-ux-final-status.json`, 복구 캡처 `artifacts/modbus-ux-restore.json`.
GUI에서 시험 설정을 사용했으며 공유 프로필의 바이트 동일성은 검증 범위가 아니다.

실물 여러 슬레이브, RS-485 전기적 타이밍, 광학 LED 확인, 장시간 soak는 이번 검증에 포함하지 않는다.
검증 산출물·백업·빌드 디렉터리는 Git 제외 경로에 보관하며 소스와 문서만 커밋한다.
