# 2026-11-14 실제 보드 사용자 시나리오 검증

**사용자 흐름 13개 + 명령 실행기 경계 16개, 총 29개 실물 사례가 모두 통과했다.**
Computer Use와 화면 자동 조작은 사용하지 않았다. 버튼의 실제 ICommand/화면 ViewModel 동작과
생산 SerialService·ModbusRtuMonitorService를 실제 UART에 연결했다. 일반 TX 시퀀스로 새 폴링·명령 실행기를 대신하지 않았다.
이번 범위에서는 앱 동작 결함이 재현되지 않아 제품 코드는 수정하지 않았다. 재실행 가능한 검사기와 독립 검산·기록을 추가했다.

## 대상과 실행 범위

- COM3 / STMicroelectronics STLink VCP, 115200 / 8N1 / handshake None, native idle 40ms.
- 실제 `BUILD?`와 `ID?`로 NUCLEO-F103RB / STM32F103RB / SERIAL-LAB/1,
  FW **1.7.1 / 22F524185C55784C**를 확인한 뒤 RAM 쓰기를 시작했다.
- [build-boot-guide manifest](build-boot-guide/manifest.json)의 BIN SHA256:
  `7FD57B2CEE1A887B74239030669A67A78188DFD56F85BBDE206D06F5F8A75980`.
  이번에 Flash를 읽어 해시를 대조하거나 펌웨어를 기록한 것은 아니다.
- 생산 소스 기준 `5808199` (직전 UX 개선 `c19a698` 포함). 실행 어셈블리와 핵심 소스 SHA256을 보고서에 보관했다.
- CLI의 발견 항목 중 하나는 실행 중인 testhost의 임시 등록이었으며 status는 instance_not_found였다.
  실제 별도 미리보기 앱은 COM300 설정의 미연결 상태였다. 그 앱은 조작하거나 종료하지 않았다.
- CLI가 모드버스 그룹/명령 제어를 제공하지 않아 저장소의 직접 UART 검증 경로를 확장했다.
  ViewModel의 await와 갱신은 전용 단일 스레드 컨텍스트에서 실행했다. Windows 창/컨트롤은 만들지 않았다.

## 실제 사용자 흐름 13개

| 흐름 | 관찰·판정 |
| --- | --- |
| 식별과 시작 값 보관 | 정확한 ID/BUILD 대조, RAM 4워드·코일 16개·쓰기 카운터 보관. 리셋 없음. |
| 대상·항목 정의·프로필 저장 | 생산 ViewModel의 설정·항목 적용과 SaveSettingsCommand 실행. TX 0. 저장 콜백은 시험 폴더의 전용 파일로 격리. |
| 한 번 읽기 | FC03 주소 0, 수량 2 → 실제 행의 원시 값 250/100, 작업 종료와 상태 일치. |
| 원시 값 쓰기 | FC10 `123 456` → 앱 재조회 검증과 별도 FC03 조회 모두 일치. |
| 항목 값 쓰기 | Int16 ×0.1 항목에 25.3 → 원시 253, 미리보기·쓰기·재조회 일치. |
| 직접 코일 쓰기 | ON/OFF 각각 FC05 + FC01 재조회. FC02/FC04의 LED 상태도 각각 1/0으로 일치. 빛의 육안 검증은 아님. |
| 읽기 전용 보호 | FC04에서 쓰기 ICommand 비활성화, 호출해도 TX 0. |
| 5그룹 폴링 | 카운터·RAM·LED·고정 패턴 4개 그룹 정상 갱신. 다섯 번째 잘못된 주소만 실제 예외 02. 선택하지 않은 오류의 집계/이름 표시, 선택 그룹 변경과 카운터 증가 확인. |
| 폴링 중 쓰기·명령·재개 | 선택한 RAM 그룹의 대기 쓰기 999 검증. LED ON/OFF 두 단계의 쓰기/상태 확인 4교환 사이에 폴링이 끼지 않음. 이후 4그룹 모두 재개. |
| ACK 전용 명령과 최근 결과 | 실제 쓰기 응답 수신, 결과는 Acknowledged. 다른 명령 선택과 후속 읽기 뒤에도 원래 명령 대상·상세 결과 보존. |
| 조건 미충족 | LED OFF에 ON을 기대하면 NotVerified, ACK는 보존하고 뒤 RAM 쓰기는 취소. 연결 유지. |
| 상태 확인 중 정지 | StopCommand 실행. 첫 쓰기의 ACK 보존, 다음 쓰기 없음, 소유권 해제와 비실행 상태 일치. |
| 종료와 원복 | RAM 4워드·코일 16개가 시작 값과 일치. LED OFF, fault=NONE, pending=0, quiet RTU, 포트 종료. |

다중 그룹은 **보드 한 대의 서로 다른 영역**으로 시험했다. 여러 물리 슬레이브가 연결된 버스 시험은 아니다.
폴링 정지 뒤 250ms 동안 추가 TX가 없고 정상 연결의 소유권이 해제되는 것도 확인했다.

## 추가 명령 실행기 16개

기존 `commands-live` 검사를 같은 최신 생산 코드로 실행했다.
ACK 전용, Float32 1.5의 FC10/재조회, 순차 6→7→15, FC04/FC02 상태 확인,
조건 미충족·잘못된 쓰기 주소의 다음 단계 차단, 지연 ACK 중 정지, 상태 조회 중 정지,
DROP·CRC·1200ms 늦은 ACK에서 미확인 쓰기 1회 반영, 폴링 중 원자적 명령과 공정한 재개를 포함한다.

지연 ACK 정지에서는 20ms 뒤에도 작업과 Stop이 완료되지 않고 소유권이 유지됐다.
경쟁 송신은 0바이트로 거절됐고 전체 ACK 수신·작업 종료 뒤에 소유권이 풀렸다.
예상한 CRC 손상 1회는 실제 응답 전체를 별도 계산해 확인했으며, 장치/직렬 드라이버의 정상 CRC로 잘못 판정하지 않았다.
불확실한 쓰기는 재전송하지 않았다. 각 오류 뒤 원래 응답과 보드 쓰기 카운터를 확인한 후 정해진 다음 사례로 진행했다.

## 독립 검산·부작용·증거

Python 표준 라이브러리만 사용하는 [독립 검산기](tests/ModbusMonitorCheck/audit-live.py)로
원시 TX/RX의 CRC·길이·장치/기능·주소·값·코일 미사용 비트·RAM/코일 상태·쓰기 카운터를 다시 대조했다.
고의 응답 CRC 오류, RX 누락, 중복 쓰기를 넣은 증거 사본 3종은 모두 거절했다.
[부정 검사 결과](../../artifacts/modbus-user-scenarios-20261114/negative-audit-checks.json)는 장치에 접근하지 않은 검사다.

| 검사 | 사용자 흐름 | 명령 실행기 |
| --- | ---: | ---: |
| 실제 통과 사례 | 13 | 16 |
| 정상 RTU 읽기 | 57 | 108 |
| 유효 쓰기 | 12 | 32 |
| 계획한 주소 예외 | 1 | 1 |
| 계획한 응답 CRC 손상 | 0 | 1 |
| 실제 쓰기 카운터 | 0 → 12 | 12 → 44 |

준비/원복을 포함한 **유효 쓰기 44회와 펌웨어 카운터 증가 44가 정확히 일치**한다.
카운터는 원복하거나 초기화하지 않았다. MCU 리셋·RTU RESET·Flash 기록은 없었다.
증거 초과와 SerialService 직렬 오류는 두 실행 모두 0이다. RAM·코일은 각 실행 전후 모두 0으로 일치했다.

로컬 증거 (생성물은 Git 제외):

- [사용자 흐름 report](../../artifacts/modbus-user-scenarios-20261114/user-flows-01/report.json),
  [원시 UART](../../artifacts/modbus-user-scenarios-20261114/user-flows-01/wire.jsonl),
  [독립 검산](../../artifacts/modbus-user-scenarios-20261114/user-flows-01/independent-audit.json).
- [명령 실행기 report](../../artifacts/modbus-user-scenarios-20261114/command-engine-01/report.json),
  [원시 UART](../../artifacts/modbus-user-scenarios-20261114/command-engine-01/wire.jsonl),
  [독립 검산](../../artifacts/modbus-user-scenarios-20261114/command-engine-01/independent-audit.json).
- 각 폴더의 sha256.txt, 상태별 JSON과 TX/RX BIN, 상위 summary.json에 시각·해시·전체 수치를 보관했다.
  실행 뒤 검사기의 실패 정리 안내 문구만 일반화했으며, 실기에 사용한 Program.cs는 상위 tested-validator-program.cs에 보존했다.

공유 기본 프로필의 전후 SHA256은 일치하지 않았다. 해당 파일의 수정 시각은 두 실기 종료 뒤였고 별도 미리보기 GUI도 실행 중이었다.
이 검사기는 사용자 프로필 저장 대신 시험 전용 저장 콜백만 사용했다. 동시 변경을 덮어쓰지 않도록 공유 파일을 복원하지 않았다.
[전후 해시 기록](../../artifacts/modbus-user-scenarios-20261114/profile-comparison.json)을 참고한다.

실제 화면 렌더링/클릭·포커스·테마, 광학 LED·물리 버튼, RS-485 전기/DE/정밀 타이밍과 장기 부하는 미검증이다.

## 재실행

해당 보드의 COM 연결만 다른 앱에서 닫고 새 증거 폴더를 지정한다. Offline은 장치에 접근하지 않는다.

```powershell
.\scripts\verify-modbus-user-scenarios.ps1 -Mode Offline
.\scripts\verify-modbus-user-scenarios.ps1 -Mode Live -Port COM3 -Manifest .\build-boot-guide\manifest.json -EvidenceDirectory .\results\user-flows-new
python .\tests\ModbusMonitorCheck\audit-live.py .\results\user-flows-new
```

펌웨어 폴더에서 실행한다. 다른 BUILD이면 쓰기 전에 중단하며, 실패하면 포트만 닫고 증거를 보존한다.
실패를 이유로 자동 재시도/초기화/원복하지 않는다. 정상 최종 사례에서만 보관한 RAM/코일 값을 복원한다.
