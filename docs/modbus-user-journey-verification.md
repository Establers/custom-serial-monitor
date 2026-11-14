# 2026-11-14 Modbus 실제 사용자 흐름 재검증

사용 순서로 점검해 제품 문제 두 건을 수정했다. 관련 자동 회귀 549건과 실제 UART 사용자 흐름 15건·명령 경계 16건이 통과했다. 화면 클릭 검증과 UART 검증은 서로 다른 세션이 맡았으며, 이 문서는 아래 자동·실물 증거 범위를 기록한다.

## 발견하고 수정한 문제

| 사용자 행동 | 이전 동작 | 수정과 회귀 경계 |
| --- | --- | --- |
| 직접 쓰기 값을 입력한 뒤 장치 ID·읽기 기능·시작 주소·수량 변경 | 이전 대상의 쓰기 값이 남아 새 대상으로 보낼 수 있었음 | 입력을 지우고 쓰기 주소를 현재 시작 주소에 맞춤. 같은 값 재할당·표시/시간 설정 변경·명시적 쓰기 주소 수정은 보존. 진행 중 작업은 기존 캡처 대상을 유지. |
| 값이 표시된 그룹에서 마지막 그룹 삭제·유효하지 않은 그룹 선택 | 행은 비었지만 `EmptyVisibility` 변경 통지가 없어 설정 안내가 갱신되지 않을 수 있었음 | 행 초기화 시 빈 상태 통지. 빈 목록·없는 ID·미선택·누락/잘못된 설정·다른 정상 그룹 전환 검증. |

제품 수정: `7433ba5`. 새 통합 회귀는 21건(대상 전환 17 + 편집/적용/저장/실행 4)이다.

## 사용자가 밟는 흐름과 검증 결과

| 흐름 | 확인한 결과 |
| --- | --- |
| 명령 새로 만들기 → 단계에 값 6·7·15 입력 → 단계 추가 → 모니터에 적용 | 유효한 단계만 자동 포함. 마지막 손대지 않은 빈 단계는 제외. 생성한 ID·대상·단계 순서 보존. 적용 자체는 송신하지 않음. |
| 편집 중 잘못된 값 입력 → 선택 시도 → 수정 또는 취소 | 잘못된 선택은 거부, 수정 후 올바른 명령 실행 대상으로 복구. 취소 시 원래 명령과 저장 파일에 변경 없음. 복제본·적용 결과와 원본 객체 분리. |
| 프로필 저장 → 전용 파일에서 다시 불러오기 | 실제 `ProfileService.SaveAsync/LoadAsync` 사용. 10개 명령과 선택 ID 보존. 새로운 모니터 ViewModel 구성까지 TX 0. |
| 저장된 명령 선택 → 명시적으로 실행 | 아래 10개 명령을 실제 생산 `ExecuteCommand` 경로로 실행. 모든 단계 ACK와 결과 조건 확인, 실제 이름이 최근 결과에 유지. 명령 결과가 직접 쓰기 입력의 상태/오류에 섞이지 않음. |
| 직접 읽기·원시 쓰기·항목 값 쓰기 | FC03 주소 0에서 250/100. FC10 123/456 및 Int16×0.1의 공학 값 25.3 → 원시 253. 생산 쓰기 검증과 별도 실제 읽기가 일치. |
| 읽기 전용 영역에서 쓰기 시도 | FC04 쓰기 실행 불가, 호출해도 TX 0. |
| 5개 영역 폴링 중 한 영역만 잘못된 주소 | 잘못된 그룹만 예외 02. 다른 4개 그룹 갱신 지속, 선택 전환·오류 집계·정지 확인. 보드 한 대의 여러 영역 시험. |
| 폴링 중 직접 쓰기·명령 실행 | 대기 쓰기 검증, 명령의 쓰기/상태 조회 사이에 폴링이 끼지 않음. 명령 종료 후 그룹 폴링 재개. |
| 조건 미충족·잘못된 주소 | 실패/미확인 결과와 수신한 ACK 유지, 뒤 단계 쓰기 차단. |
| 상태 조회 중 정지·지연 ACK 중 정지 | 다음 단계 쓰기 없음. 이미 전송한 요청의 정리 전에는 소유권 유지, 경쟁 TX 거부. 정지 완료 뒤 소유권 해제·추가 TX 없음. |
| 응답 DROP·CRC 손상·1200ms 늦은 ACK | 미확인 상태를 성공으로 표시하지 않고 쓰기 재전송 없음. 펌웨어 쓰기 카운터로 각 쓰기 1회 반영을 별도 확인. |

편집 → 적용 → 저장 → 복원 → 명시적 실행은 생산 편집/모니터 ViewModel과 실제 프로필 파일을 연결한 자동 검사다. 그 검사의 기록용 통신 서비스는 실행 승인만 기록하며 장치 응답을 흉내 내지 않는다. 아래 실물 검사는 생산 `SerialService`, `ModbusRtuMonitorService`, `ModbusRtuViewModel`의 실제 ICommand 경로를 별도 단일 스레드 컨텍스트에서 실행한다.

`모니터에 적용`은 현재 앱에서 사용할 정의를 반영하고, `프로필 저장`은 다음 실행을 위해 파일에 보관한다. 둘 다 장치 제어가 아니다. 실제 앱의 프로필 불러오기는 연결 중 허용되지 않으므로 **정지 → 저장 → 연결 해제 → 불러오기 → 다시 연결 → 명시적 읽기/실행** 순서다. 이번 UART 검사에서 프로세스를 재시작하거나 MainWindow의 불러오기 버튼을 누른 것은 아니다.

## 저장된 펌웨어 명령 10개: 실제 장치 결과

| 명령 | 독립 확인 결과 |
| --- | --- |
| LED 켜기 | FC05 coil 0x1000=1, 별도 FC04 0x1007=1 |
| LED 끄기 | FC05 coil 0x1000=0, 별도 FC02 0x1001=0 |
| LED 켜기 → 끄기 | 각 쓰기 직후 위 별도 상태 확인, 최종 OFF. 지연 없는 순차 제어이며 점멸 주기 시험은 아님. |
| UInt16 123 | 0x1010 = 123 |
| Int16 -123 | 0x1011 = 0xFF85 |
| Int16 ×0.1, 25.3 | 0x1012 = 253 |
| UInt32 305419896 | 0x1014..15 = 0x1234, 0x5678 |
| Float32 1.5 | 0x1016..17 = 0x3FC0, 0x0000 |
| Int32 -123456789 | 0x1018..19 = 0xF8A4, 0x32EB |
| RAM 6 → 7 → 15 | 0x1010에서 매 단계 FC03 재조회가 각각 6, 7, 15 |

실제 마지막 FC06 RX `01 06 10 10 00 0F CC CB`를 값 해석기에 넣어 `+0: 15` 표시도 확인했다. 분류는 `RequestOrResponse` 그대로다. FC06 요청과 정상 응답의 바이트 형식이 같아 RX만으로 역할을 확정하지 않지만, 주소 4112와 단일 레지스터 값 15는 해석할 수 있다. 별도 FC03 재조회가 장치 값 검증이다.

## 실물 증거와 독립 검산

COM3 STMicroelectronics STLink VCP, 115200 / 8N1 / no handshake / idle 40ms. 쓰기 전에 실제 ID/BUILD로 SERIAL-LAB/1, FW **1.7.1 / 22F524185C55784C**를 확인했다. 제품 소스는 `7433ba5` 기준이며 검사기 확장 소스와 실행 어셈블리 SHA256은 각 report에 보관했다.

| 결과 | 사용자 흐름 | 명령 경계 |
| --- | ---: | ---: |
| 실물 사례 | 15 PASS | 16 PASS |
| 정상 RTU 읽기 | 92 | 108 |
| 유효 쓰기(준비·원복 포함) | 26 | 32 |
| 계획한 주소 예외 | 1 | 1 |
| 계획한 응답 CRC 손상 | 0 | 1 |
| 쓰기 카운터 | 55 → 81 | 81 → 113 |

유효 쓰기 총 58회와 카운터 증가 58이 일치한다. RAM 0x1000..03, 코일 0x1000..0F, 프리셋 RAM 0x1010..19를 보관/복원했다. 특히 기존 0x1010의 **15**도 15로 복원했다. 카운터는 초기화/복원하지 않았다. MCU/RTU RESET·Flash 기록 없음. 종료 시 LED OFF, fault=NONE, pending=0, quiet RTU, 포트 닫힘. 두 증거 모두 overflow=false, serialErrors=[]다.

Python 독립 검산기는 원시 TX/RX의 CRC·길이·기능·주소·값, 단계별 쓰기→결과 조회→후속 쓰기 순서, 카운터와 최종 복원을 대조한다. 프리셋 13단계의 결과 조회를 포함한 46개 TX 전체 순서를 고정한 독립 계약으로 검사했다. 최종 값만 맞고 중간 검증이 빠진 경우를 통과시키지 않는다.

원본은 보존했다. 강화한 검산기는 원본과 바이트까지 동일한 대조 사본에서 통과했다(`wire.jsonl`, `report.json`, `user-profile.json` 대조). CRC 손상·응답 삭제·쓰기 복제·중간 단계 재조회 삭제·FC04 상태를 FC01 코일 조회로 치환한 사본 5종은 모두 거절했다. 뒤 두 사본은 단계별 검증 순서 조건에서 거절됐다. 부정 검사는 장치에 접근하지 않는다.

로컬 생성물(Git 제외):

- [사용자 흐름 원본](../artifacts/modbus-user-journeys-20261114/live-01/report.json), [UART](../artifacts/modbus-user-journeys-20261114/live-01/wire.jsonl)
- [강화된 독립 검산](../artifacts/modbus-user-journeys-20261114/audit-checks-03/unchanged/independent-audit.json), [검산기 검증 6건: 정상 대조 1·부정 5](../artifacts/modbus-user-journeys-20261114/audit-checks-03/results.json)
- [명령 경계](../artifacts/modbus-user-journeys-20261114/commands-01/report.json), [독립 검산](../artifacts/modbus-user-journeys-20261114/commands-01/independent-audit.json)
- [자동 회귀 549건](../artifacts/modbus-user-journeys-20261114/results/user-journeys.trx)

사용자 기본 프로필은 읽거나 덮어쓰지 않았다. 실기 저장은 증거 폴더의 `user-profile.json`만 사용했다. 별도 GUI 검증 세션의 앱/프로필 조작과 분리했다. 실제 다중 슬레이브·광학 LED·물리 버튼·RS-485 전기 특성·장기 부하는 이번 범위에 없다.

검산기 사본의 출력 폴더는 입력 증거 폴더 바깥만 허용한다. 입력 안을 지정하면 복사/파일 생성 전에 거부함을 확인했다.

## 최신 소스 통합 확인

폼 정렬 수정 `91f1031`과 이번 수정/검증을 `fbfd6a0`에서 통합해 main에 반영했다.

- 전체 Release 솔루션 빌드: 경고 0, 오류 0.
- 전체 솔루션 자동 검사: Core 723 PASS, WinUI 2,328 PASS / 조건부 17 SKIP, 실패 0. 합계 3,051 PASS.
  [집계](../artifacts/modbus-user-journeys-20261114/integrated-results/summary.json),
  [Core TRX](../artifacts/modbus-user-journeys-20261114/integrated-results/core-tests.trx),
  [WinUI TRX](../artifacts/modbus-user-journeys-20261114/integrated-results/winui-tests.trx).
- 최종 포터블과 ZIP 재생성 및 payload 검사 통과. 실행 파일은
  `release/SerialMonitorPortable/SerialMonitor.WinUI.exe`, DLL SHA256은
  `0222A732EC5C5C91E97AFDA1675BE93E6254F31EA56B0688B19942169CE5B6FA`.

별도 UI 담당 세션의 `91f1031` 검증은 실제 WinUI 레이아웃 측정 82사례, 공통 폼 140건, 스크롤 끝 도달 62건 통과다. 이 측정과 Computer Use 클릭/육안 확인은 구분한다. [정렬 검증 기록](modbus-numeric-field-layout.md#최종-검증-2026-11-14)을 참고한다.

## 재검증

기존 [실기 실행 지침](../firmware/nucleo-f103rb-serial-lab/MODBUS-USER-SCENARIO-VALIDATION.md#재실행)을 따른다. 새 증거 폴더만 허용하며 실패 시 자동 재전송·리셋·복원을 하지 않는다. 아래는 이미 수집한 증거만 검사하므로 UART 송신이 없다.

```powershell
python firmware/nucleo-f103rb-serial-lab/tests/ModbusMonitorCheck/audit-live.py <새_실기_증거폴더>
python firmware/nucleo-f103rb-serial-lab/tests/ModbusMonitorCheck/audit-negative.py <v2_사용자_증거폴더> <새_부정검사_폴더>
```
