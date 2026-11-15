# PC 장치 시뮬레이터 실제 UART 검증 — 2026-11-15

## 검증 대상과 결과

실행 중인 Release 앱(소스 기준 `6b07c44`)과 동일한 `SerialMonitor.WinUI.dll` 및 `SerialMonitor.Core.dll`을
명령행 검사기에 참조·복사하고 SHA-256 일치를 확인했다. 앱의 COM3 연결을 CLI로 닫은 동안 검사기만 포트를 사용했다.
검사기는 생산 `SerialService`/`ModbusSensorService`를 실행하고, 실제 NUCLEO-F103RB가 마스터 요청을 전송했다.
보드도 수신한 PC 응답 전체를 고정 바이트·CRC와 대조했다. Computer Use는 사용하지 않았다.

- COM3 / ST-LINK UART / 115200 / 8N1 / handshake None / native idle 40ms.
- 앱 DLL SHA-256: `11BD4DD5DD65BC5B51C781D59FCD2E32768C0815C36C8EDC823469B97DBE9D1E`.
- Core DLL SHA-256: `CCAEDE564EEF2EB6EEB8FF6EBC737E962F27ABB3C7A4B985521B59EA274BF1D6`.
- 보드 FW **1.8.0 / BUILD `5412A7E884FBF935`**, BIN 22,268바이트.
- BIN SHA-256: `F15F72621188E0C6816BA71DE6BF189E31E4659CB1B306E8BD3968BF7236B8FE`.

| 시험 | 실제 결과 |
| --- | --- |
| FC06 재요청 | PC 응답 지연 600ms. 보드가 211ms 뒤 같은 요청을 다시 전송. 최초 요청 기준 674ms / 1,286ms에 두 응답 수신, PC 계속 실행 |
| FC22 재요청 | 같은 조건으로 657ms / 1,260ms에 두 응답 수신, PC 계속 실행 |
| FC22 마스크 쓰기 | 보유 레지스터 0의 초기값 18에 AND 0x00F2 / OR 0x0025 적용. FC03 재조회 값 23 일치 |
| FC23 쓰기 후 읽기 | 주소 0–1에 0x1234 / 0xABCD 쓰기. FC23 응답과 별도 FC03 재조회 모두 일치 |
| FC24 미지원 요청 | `0118000081DF`에 `0198018A00`(Illegal Function) 응답. FIFO 기능을 구현한 것은 아님 |

총 9개 요청·9개 응답 일치. PC serialErrors=0, 증거 overflow=false. 보드의 parse_errors/rx_drops/uart_errors/tx_rejected는
시험 전후 모두 0. 각 경우는 독립된 PC 레지스터 맵으로 시작하며, 값 쓰기는 PC 시뮬레이터 메모리에만 적용했다.
마지막에 보드를 `RTU` 대기로 돌리고 앱의 COM3 연결을 복구했다(세션 2, busy=false, serialErrors=0).

## 펌웨어 기록과 사전 검사

- 기록 전 실제 UART의 ID?/BUILD?로 SERIAL-LAB/1, FW1.7.1 / `22F524185C55784C`를 확인했다.
- ST-LINK `066FFF485753667187145434`, MCU ID 0x410 / Flash 128KiB를 확인하고 전체 Flash를 백업했다.
  백업: `backups/066FFF485753667187145434-20261115-203901-536.bin`.
  SHA-256: `74B1169AF379B8404EF1E602B1DEA34D88BEA949CFCB5FB77C8A5C85B7BA2CDC`.
- ARM 빌드와 기록 후 읽기 검증·리셋 성공. 기존 기능과 새 마스터 시험의 C 검사 **5,871개**, 독립 계약 검사 **81개** 통과.
- 명령행 검사기 Release 빌드 경고·오류 0. 앱 소스를 다시 빌드하지 않아 다른 채팅의 미커밋 변경을 포함하지 않는다.

## 재실행

검사할 GUI의 COM3 연결만 `serialctl disconnect --instance <실제 인스턴스 ID>`로 닫는다.
아래 명령은 선택한 기존 앱 DLL을 그대로 검사하며 펌웨어 기록은 하지 않는다. 보드의 BUILD가 manifest와 다르면 시험을 시작하지 않는다.

```powershell
./scripts/verify-simulator.ps1 -Port COM3 `
  -AppDirectory 'C:/Users/pjh/Desktop/serial/SerialMonitor.WinUI/bin/x64/Release/net8.0-windows10.0.19041.0/win10-x64' `
  -EvidenceDirectory 'C:/Users/pjh/Desktop/serial/artifacts/simulator-hardware-new-run'
```

이 명령은 firmware 폴더에서 실행한다. 기본 manifest는 `build-simulator-master/manifest.json`이며 `-Manifest`로 명시할 수 있다.
실패하면 자동 재전송하지 않고 `report.json`의 전체 RX/TX를 확인한다. 시험의 장치 ID는 1, writable holding 주소는 0–1이다.
전체 호스트 검사 제한은 60초이고 보드 시험은 각 8초 이내 끝난다. 이것은 일반 제품용 명령이 아닌 해당 보드 전용 검사다.

보드 명령은 LAB의 유휴 상태에서만 허용한다. `MASTER RETRY6`/`RETRY22`는 ACK 뒤 1초 대기하고 같은 요청을 두 번,
211ms 간격으로 전송한다. `MASTER ADVANCED`는 응답 확인 후 다음 요청을 보내며 응답 후 10ms를 기다린다.
CRC를 포함한 응답 불일치·예상 밖 RX·TX 큐 거절·시간 초과는 실패로 저장하며 재전송하지 않는다.
실행 중의 바이트는 응답으로 취급하므로 별도 콘솔 명령을 섞지 않는다. 완료 후 `MASTER?`로 마지막 결과를 조회한다.

조회용 시퀀스 **F103RB LAB - Last simulator master result**를 CLI로 등록했다(`persisted=true`, `replaced=false`).
원본은 [시퀀스 JSON](sequences/modbus/simulator-master-status.cli.json)이다. 이번 작업에서 추가 실행하지 않았고,
사용 시 앱의 직접 시험 역할에서 LAB → BUILD? → MASTER? → RTU 순서로 마지막 결과만 조회한다. 새 시험을 시작하지 않는다.

## 증거와 검증 한계

- 최종 전체 RX/TX·보드 보고·DLL 해시: `artifacts/simulator-hardware-20261115-verified/report.json`(저장소 루트 기준).
- 최종 실행 로그: `artifacts/simulator-hardware-verified.log`.
- ARM/C/계약/Flash 로그: `artifacts/simulator-master-arm-build.log`, `simulator-master-native.log`, `simulator-master-contract.log`, `simulator-master-flash.log`.
- 앱 복구 상태: `artifacts/simulator-live-final-status.json`. 테스트 전 CLI의 관찰 역할 송신 차단도 별도 캡처로 보존했다.
- 초기 `run1`은 native idle을 사용하지 않은 예비 검사다. 당시 보고서의 고정 `byteTimeoutMs:40` 표기는 부정확하며,
  설정을 실제 적용값으로 기록하고 앱 DLL 두 개를 모두 고정한 **verified 결과만 최종 근거**로 사용한다.
- ST-LINK UART에서 실제 보드와 교환한 결과다. WinUI 버튼·렌더링, RS-485 2선식의 전기적 충돌/방향 전환,
  정밀 t1.5/t3.5 적합성과 장시간 운전은 검증하지 않았다.

## 보고서 종료 처리 회귀 검사

검사기는 drain과 시리얼 종료 오류를 기존 시험 실패에 덧붙이고, RX 구독 해제 후 잠금 안에서 수신 기록을 확정한다.
진행 중인 RX 콜백의 큐 실패까지 포함한 복사본만 직렬화하며, 종료 뒤 호출된 콜백은 기록·큐에 진입하지 않는다.
최종 overflow나 시리얼 오류가 있으면 `passed`와 `hardwareVerified`는 false다.
종료 시 적용 통신 설정은 초기화되므로 보고서에는 종료 직전 설정을 보존한다.

저장소 루트에서 장비 없이 실행한다:

```powershell
dotnet run --project firmware/nucleo-f103rb-serial-lab/tests/SimulatorCheck.Regression/SimulatorCheck.Regression.csproj -c Release
```

JSON 생성 중 RX, 진행 중 RX의 큐 실패, drain·종료 오류, 최종 오류 카운터, 정상·비정상 취소를 검증한다.
이 종료 처리 보완은 COM3 접속·송신·펌웨어 변경 없이 검증했으며 위 실기 결과는 기존 시험 기록이다.
