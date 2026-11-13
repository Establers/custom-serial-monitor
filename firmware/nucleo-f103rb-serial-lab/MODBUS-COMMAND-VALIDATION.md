# 2026-11-14 명령·결과 확인 실물 UART 검증

COM3의 NUCLEO-F103RB / SERIAL-LAB/1에서 **16개 사례가 모두 통과**했다.
대상은 실제 `ID?`·`BUILD?` 응답으로 확인한 FW **1.7.1 / 22F524185C55784C**이며,
115200 / 8N1 / handshake None / native idle 40ms를 사용했다.

## 실행 경로와 결과

새 `commands-live` 검사는 최신 소스의 실제 `SerialService`와
`ModbusRtuMonitorService.ExecuteCommandAsync` / `GetCommandSnapshot`을 사용한다.
일반 TX 시퀀스로 새 명령 실행기를 대신하지 않는다. 검사기 Release 빌드는 경고·오류 0이었다.
기존 독립 패킷·경계·GUI 가져오기 fixture의 Offline 검사도 통과했다. Offline은 실물 시험이 아니다.

| 사례 | 관찰·판정 |
| --- | --- |
| 대상 식별·초기 상태 | 정확한 ID/BUILD 대조, RAM 4워드·코일 16개·쓰기 카운터 보관 |
| 응답만 확인 | 쓰기 한 번, ACK 수신, 결과는 Acknowledged이며 동작 검증으로 표시하지 않음 |
| Float32 | FC10으로 `1.5 → 3FC0 0000` 쓰기, FC03 전체 두 워드 재조회 일치 |
| 순차 명령 | FC06 `6 → 7 → 15`, 각 단계 재조회까지 정확한 TX/RX 순서, 쓰기 카운터 +3 |
| 별도 상태 확인 | LED ON 후 FC04 `0x1007`의 마스크 1, LED OFF 후 FC02 `0x1001`의 비트 0 확인 |
| 상태 조건 불일치 | 정상 0 응답을 받고 NotVerified, 뒤 단계는 Canceled, 센티널 쓰기 없음 |
| 잘못된 쓰기 주소 | 정확한 예외 02, ACK로 오인하지 않음, 연결 유지·뒤 단계 미송신 |
| 지연 ACK 중 정지 | 300ms ACK를 끝까지 수신할 때까지 소유권 유지, Stop 반환 시 작업 완료·소유권 해제 |
| 정지 중 경쟁 송신 | 경쟁 요청 거부, 정지 후 조용한 구간에도 지연 송신 없음, 실제 TX 바이트 수 일치 |
| 상태 조회 중 정지 | 이미 받은 쓰기 ACK 보존, 다음 쓰기 없음, 연결 유지 |
| DROP·CRC 오류 | 실제 쓰기는 각각 1회 반영, 미확인 상태와 연결 종료, 자동 쓰기 재시도 없음 |
| 늦은 ACK | 150ms 제한 뒤 연결 종료, 재연결에서 1200ms 지연 ACK 전체를 먼저 관찰, 새 요청으로 오인하지 않음 |
| 폴링 중 명령 | 두 그룹의 폴링 사이에 전체 순차 명령이 연속 실행되고 이후 두 그룹 모두 재개 |
| 원복 | RAM 4워드·코일 16개가 초기값과 일치, 오류 해제·조용한 RTU, 포트 종료 |

위 표의 결합 항목을 나누어 검사한 총 사례 수는 16개다. 명령 완료 상태와 소유권은
검사기의 Dispose 정리 **전에** 확인하여, 정리 동작이 조기 완료 결함을 숨기지 않게 했다.

## 독립 검산과 증거

- RTU 읽기 108회, 유효 쓰기 32회, 고의 잘못된 주소 1회, LAB 제어·식별 38회.
- 유효 쓰기 32회와 보드 쓰기 카운터 **0 → 32**가 일치했다.
  준비·원복 쓰기도 포함한 수치이며, 명령만의 카운터와 혼동하지 않는다.
- 원시 TX/RX의 CRC·길이·주소·값·사용하지 않는 비트와 의도한 CRC 손상을 별도 Python 검사로 다시 대조했다.
- 직렬 오류 0, 증거 버퍼 초과 없음. RX 관측은 콜백에서 메모리에 제한 보관하고 파일 저장은 별도 단계에서 했다.
- MCU/RTU RESET·Flash 기록은 실행하지 않았다. 누적 읽기·쓰기 카운터는 원복하지 않았다.

로컬 증거: [report.json](../../artifacts/modbus-command-uart-20261114/engine-01/report.json),
[wire.jsonl](../../artifacts/modbus-command-uart-20261114/engine-01/wire.jsonl),
[독립 검산](../../artifacts/modbus-command-uart-20261114/engine-01/independent-audit.json),
[SHA256 목록](../../artifacts/modbus-command-uart-20261114/engine-01/sha256.txt).
원본·스냅샷은 같은 로컬 폴더에 보관하며 Git에는 포함하지 않는다.
report.json SHA256: `9E81811720FEA78F4628291DD1E176EF18E00967456898A55AA66C60FD8FBF43`.

소스 기준은 `4b9b273`이며, 실행 보고서에는 검사기 변경 경로·핵심 소스 SHA256·실행 어셈블리 SHA256을 기록했다.
보드 BUILD 응답과 manifest의 일치를 확인한 것이며 이번에 Flash를 읽어 해시를 검증한 것은 아니다.
이 검사는 실제 UART의 명령 실행기 시험이다. GUI 버튼/표시 검증, LED 빛의 육안 확인,
RS-485 전기 특성·DE 전환·장기 부하는 별도 범위다.

## 재실행

검증된 보드의 포트를 다른 앱에서 닫고, 해당 이미지의 manifest와 새 증거 폴더를 명시한다.

```powershell
.\scripts\verify-modbus-commands.ps1 -Mode Live -Port COM3 -Manifest .\build-boot-guide\manifest.json -EvidenceDirectory .\results\commands-new-run
```

스크립트는 대상 manifest의 정확한 펌웨어 버전·BUILD를 요구한다. 다른 이미지에 맞춰 임의로 우회하지 않는다.
실패·취소 시에는 포트만 닫고 증거를 남긴다. 불확실한 쓰기를 재전송하거나 자동 초기화하지 않는다.
정상 통과한 마지막 사례에서만 보관한 RAM·코일 값을 명시적으로 복원한다.
