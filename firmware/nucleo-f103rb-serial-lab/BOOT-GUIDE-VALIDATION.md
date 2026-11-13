# 1.7.1 — 리셋 시 한 번 출력하는 테스트 안내

2026-11-13에 NUCLEO-F103RB / ST-LINK SN `066FFF485753667187145434` / COM3에서 확인했다.
Computer Use나 GUI 화면 조작은 사용하지 않았다.

## 변경 동작

- 전원 인가·실제 MCU 리셋으로 하드웨어 시작 코드가 실행될 때 UTF-8/CRLF 안내문을 한 번 큐에 넣는다.
- 연결 설정, 자동 시퀀스 파일·26개 검사 기대값, 카운터·직접 쓰기·LED 주소와 LAB/메모리 부하 명령을 안내한다.
- 전체 1,112바이트를 한 번에 큐에 넣으며 1,536바이트 상한을 컴파일 시 검사한다. TX backpressure는 부분 출력 없이 재시도하고, 성공 후에는 재출력하지 않는다.
- 안내문 이후에는 조용한 Unit 1 RTU다. `LAB`/`RTU` 전환·재연결·`RTU RESET`으로는 안내문이 다시 나오지 않는다.
- 리셋 전에는 폴링·브리지를 정지한다. 연결 후 Terminal / UTF-8에서 RESET을 눌러 읽는다. HEX 화면에서는 안내문도 HEX 바이트로 보인다.

## 보드 기록

| 항목 | 확인 값 |
| --- | --- |
| 이전 실제 BUILD | 1.7.0 / BD6B8BF1A4C200D7 |
| 새 펌웨어 / BUILD | 1.7.1 / 22F524185C55784C |
| BIN 길이 / SHA256 | 20,696B / `7FD57B2CEE1A887B74239030669A67A78188DFD56F85BBDE206D06F5F8A75980` |
| Flash / RAM 영역 사용 | 20,696B / 16,456B; RAM에는 기존 4KiB stack + 256B heap 예약 포함 |
| 기존 Flash 백업 | [128KiB 원본](backups/066FFF485753667187145434-20261113-222411-388.bin) |
| 백업 SHA256 | `122B17F0F4CCC4561559D8289F809816FE2DDB942A44243CD07D9787FD66BD65` |

`flash.ps1`이 probe·device ID 0x410·128KiB 용량을 확인하고 백업한 뒤 기록했다.
STM32CubeProgrammer의 `Download verified successfully`와 reset 성공을 확인했다.
다른 장치·앱 설치·공개 릴리즈는 변경하지 않았다.

## 검증

- ARM 빌드 및 MSVC `/W4 /WX /utf-8` 네이티브 검사 **5,831개 PASS**.
- 고정 데이터·바이너리 경계 독립 검사 **81개 PASS**, 기존 RTU/MS/TP·GUI/CLI 파일 형식 검사 PASS.
- [리셋 안내 실기 보고서](results/boot-guide-live-20261113/report.json): **21개 교환 PASS**.
  실제 `RESET CONFIRM` 두 회 모두 ACK 10바이트 + 안내문 1,112바이트가 정확히 일치했다.
  각 리셋 후 추가 대기에는 출력이 없고, 다음 FC03 응답은 기존의 9바이트와 같았다.
  포트 재연결 및 `LAB`/`RTU`/`RTU RESET`/상태 조회에도 안내문이 섞이지 않았다.
- [기존 모니터 실기 보고서](results/boot-guide-monitor-20261113/report.json): **24개 PASS**.
  폴링 105회·쓰기·오류 주입·소유권을 확인하고 기존 52단계 GUI 정의도 같은 실제 실행·RX 판정 경로로 두 번 재생했다.
  두 회 모두 RX Check 26개 일치(약 19.15초 / 19.23초), 미일치·판정 불가 0개다.
- 마지막에는 RAM/카운터 초기화·LED OFF·오류 해제·조용한 RTU이며 COM3를 닫았다.

빌드 산출물과 native 안내 원본은 `build-boot-guide/`에 있다. `results/`의 각 TX/RX `.bin`을 포함해 전체 바이트를 판정했다.
이 로컬 증거 폴더와 백업은 Git에 포함하지 않는다. 실제 GUI 렌더링·물리 RESET 버튼 누르기·장시간 내구는 수행하지 않았다.

## 재현

펌웨어 폴더에서 실행한다. Live 명령은 해당 COM의 GUI 연결이 닫혀 있어야 하며 실제 MCU를 두 번 리셋한다.
일치하는 보드 BUILD를 확인한 뒤 진행하고, 실패한 TX는 자동 재전송하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1 -BuildDirectory build-boot-guide
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-boot-guide.ps1 -Port COM3 -BuildDirectory build-boot-guide -EvidenceDirectory ('.\results\boot-guide-' + (Get-Date -Format yyyyMMdd-HHmmss-fff))
```

이번 PC 검증은 다른 앱 빌드와 `obj`를 공유하지 않도록 자식 PowerShell에 `UseArtifactsOutput=true`,
`ArtifactsPath=C:\Users\pjh\Desktop\serial\artifacts\boot-guide-dotnet` 환경 변수를 전달해 실행했다.
