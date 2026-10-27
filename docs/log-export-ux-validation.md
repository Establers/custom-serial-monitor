# 로그 내보내기 문구·실제 화면 검증 — 2026-10-27

## 문구 결정

| 후보 | 검토 |
| --- | --- |
| 보관 로그 저장 | 이미 파일에 보관한 기록을 다시 저장한다는 느낌이 강하고 내부 용어에 가까움 |
| 표시된 로그 저장 | 현재 보이는 행 또는 필터 결과만 저장한다는 뜻으로 읽힘. 실제로는 필터에 가려진 기록도 포함하므로 부정확함 |
| 현재 로그 저장 | 스냅샷 의미는 전달되지만 실시간 Logging 시작·중지와 혼동할 수 있음 |
| 로그 내보내기… | 현재 남아 있는 기록을 별도 파일로 꺼내는 작업으로 이해하기 쉽고 실시간 Logging과 구분됨. 최종 선택 |

툴바·Log 탭·우클릭 메뉴·진행률·완료/취소 메시지·저장창 제목 및 확인 버튼의
용어를 통일했다. 툴팁에는 필터로 숨긴 기록도 포함한다는 범위를 명시한다.
실제 저장 범위는 메모리에 남아 있는 수신 기록이며 지워진 과거 기록을 복구하지 않는다.

후속 위치 요청에 따라 주 버튼을 **Start Logging / Stop Logging 바로 오른쪽**으로
옮겼다. 아래 캡처는 이동 전 검증 화면이며 진행률과 저장 동작의 검증 결과는 동일하다.

이후 저장 기준은 **내보내기 버튼 클릭 시점**으로 고정하고, 기본 파일명에
**현재 남아 있는 첫 기록 시각과 클릭 시각**을 넣도록 변경했다. 다음 후속 검증은
이 변경을 반영한 실행 결과이고, 그 아래 기존 대량/진행률 검증은 시점 변경 전이다.

## 후속 검증: 버튼 클릭 기준과 기본 파일명

- 현재 소스의 별도 Debug 빌드에서 MOCK / 115200 / Terminal / byte timeout 40ms,
  기본 Normal 수신(약 500ms마다 한 건)과 실시간 파일 저장을 함께 실행했다.
  테스트 전용 프로필 경로만 분리했고 내보내기 지연은 추가하지 않았다.
- 툴바의 내보내기 버튼이 Start Logging / Stop Logging 바로 오른쪽에 표시되었다.
- `18:43:36.519`에 내보내기를 눌렀을 때 저장창에 제안된 파일명은
  `2026-10-27_184319.672_to_2026-10-27_184336.519_logs.log`였다.
  첫 시각은 실제 첫 기록 `18:43:19.672`와 일치했다.
- 저장창을 연 채로 `18:44:02`에 MOCK에 `QA_AFTER_EXPORT_CLICK_20261027`을 전송했다.
  500ms 캡처에서 TX 성공, RX echo 수신, 누락/serialErrors 0을 확인했다.
  저장창을 약 49초 열어 둔 뒤 제안된 이름을 유지해 내보냈다.
- 결과는 클릭 이전 **34건**이며 실시간 파일의 앞 34건과 순서·본문 전체가 일치했다.
  마지막 기록은 `18:43:36.446`, 다음 실시간 기록은 `18:43:36.946`이었다.
  즉 클릭 `18:43:36.519` 이후 기록과 테스트 문자열은 내보내기에서 제외되었다.
  실시간 파일에는 테스트 문자열의 TX/RX 두 건과 후속 수신이 정상 기록되었다.
- 실시간 파일 187건, 임시 파일 잔여 0개를 확인한 뒤 테스트 로깅과 MOCK 연결을
  종료하고 이번 테스트 프로세스만 정리했다. 결과는 로컬
  `artifacts/log-export-ux/click-verification.json`에 남겼다.
- 자정 경계, UTC 입력의 로컬 시각 변환, en-US/ko-KR/ar-SA 문화권에서도
  Windows 파일명으로 유효한 동일 형식이 나오는지 회귀 테스트를 추가했다.
  관련 테스트 **21건 통과**, 최종 Release 빌드 경고/오류 0개.

## 기존 실행 검증: 대량 수신과 진행률

- 별도 테스트 실행 파일에서 `MOCK / 115200 / Terminal / byte timeout 40ms` 사용.
  기존 실행 중인 창과 실제 COM 장치는 조작하지 않았다.
- Debug Test 탭에서 Normal Lines, 목표 설정값 5,000 lines/sec, burst 100으로 RX를 계속 생성했다.
  WARN/ERROR/FAULT 문자열은 MOCK의 이벤트 주입 데이터이며 실제 장비 오류가 아니다.
- 화면에서 Start Logging과 로그 내보내기를 눌러 두 파일의 동시 기록을 확인했다.
  보관량을 50,000에서 1,000,000으로 늘리고 수신을 계속해 한도 이후 교체도 확인했다.
- 정상 빌드의 내보내기는 **577,226건 / 36,453,391바이트**였다.
  시작/끝 MOCK 번호는 310303~887528로 연속되었다. 실시간 파일에서 해당 구간을
  찾아 모든 행을 순서대로 비교했고 **577,226건 전체 일치**했다.
  해당 구간 뒤에도 실시간 파일에 1,036,572건이 더 기록되었다.
- UI에서 관찰한 RX/파일/UI 드롭 카운터는 0이었고 CLI 상태의 serialErrors도 0이었다.
  이 결과는 이번 MOCK 실행 조건의 검증이며 실물 UART/느린 USB 장기 시험은 아니다.

첫 정상 빌드의 파일 저장이 빠르게 끝나 진행률 캡처가 어려웠다. 이후에는
`artifacts/log-export-ux/slow-source` 복사본에만 256건마다 400ms 지연을 주입했다.
이는 오래 걸리는 내보내기의 UI 상태를 검증하기 위한 지연이며 실제 느린 디스크의
성능 측정이 아니다. 운영 소스에는 지연이 없으며 후반 검증은 별도 프로필을 사용했다.

## 화면에서 발견하고 수정한 문제

1. 진행률 패널이 원래 창 아래에 떠서 TX 입력·전송과 저장 명령 영역을 가렸다.
   패널을 **로그 뷰포트 오른쪽 위**로 이동했다. 툴바 버튼과 아래 입력부는 그대로
   사용할 수 있다. 진행률 47%/64%/75% 중 TX 입력과 Send를 실제로 조작했고,
   실시간 파일에서 `TX > UX_EXPORT_TX_20261027`과 동일한 RX echo를 확인했다.
2. 기존 WinRT 저장창은 선택 즉시 빈 파일을 만들어 내보내기 취소 후 0바이트 파일이
   남았다. 내보내기 전용 Windows Common Item Dialog에서 **경로만 선택**하게 바꿨다.
   파일 쓰기는 기존 백그라운드 exporter만 수행한다. 새 저장창으로 선택한
   `export-cancel-3.log`는 진행 중에도 생성되지 않았고, 취소 후 결과 파일과
   `.serial-history-*.tmp` 모두 없었다. 수신과 실시간 파일 기록은 계속되었다.
3. 저장창 제목·확인 버튼도 각각 **로그 내보내기 / 내보내기**로 바꿨다.
   새 저장창에서 재시도한 **50,000건**이 정상 완료되었고 실시간 파일 구간과 전부
   일치했다. 그 구간 뒤에도 1,119,887건이 더 기록되어 수신·저장이 유지됨을 확인했다.

저장창은 별도 STA에서 실행해 WinUI dispatcher를 막지 않는다. 정상 결과 파일은
기존 exporter가 임시 파일 쓰기·flush·close 후 최종 경로로 교체한다. 기존 파일을
추측해 삭제하는 방식이나 Windows App SDK 버전 업그레이드는 사용하지 않았다.
[Common Item Dialog](https://learn.microsoft.com/en-us/windows/win32/shell/common-file-dialog),
[WinRT 저장창의 빈 StorageFile 동작](https://learn.microsoft.com/en-us/uwp/api/windows.storage.pickers.filesavepicker.picksavefileasync).

## 화면 및 파일 증거

- [정상 빌드: 내보내기 완료 후에도 수신·실시간 저장 지속](../artifacts/log-export-ux/screenshots/01-normal-receiving-after-export.png)
- [수정 전: 진행률이 TX 영역을 가린 화면](../artifacts/log-export-ux/screenshots/02-progress-before-layout-fix.png)
- [수정 후: 진행률 75%, TX 영역 사용 가능](../artifacts/log-export-ux/screenshots/04-progress-with-tx-available.png)
- [최종 저장창으로 시작한 진행률 화면](../artifacts/log-export-ux/screenshots/05-final-progress-native-picker.png)
- [최종 50,000건 완료 및 수신 지속](../artifacts/log-export-ux/screenshots/06-final-completed-receiving.png)
- [정상 빌드 전체 내용 비교](../artifacts/log-export-ux/normal-verification.json)
- [취소 후 결과·임시 파일 부재](../artifacts/log-export-ux/cancel-verification.json)
- [최종 저장 전체 내용 비교](../artifacts/log-export-ux/final-verification.json)

테스트용 파일 로거를 flush하고 MOCK 연결을 끊은 뒤 테스트 프로세스만 정리했다.
공유 기본 프로필은 시작 전 백업과 해시가 같았다. Release 빌드는 경고/오류 0개,
로그 내보내기 및 진행률 ViewModel 관련 테스트 18개가 통과했다.
