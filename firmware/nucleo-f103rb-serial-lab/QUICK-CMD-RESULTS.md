# Quick CMD 추가 검증 — 2026-10-31

추가 회귀 수정: [Quick CMD / 수동 TX 원문 보존 검증](../../docs/quick-command-payload-validation.md).
아래의 기존 PING 예시는 앞뒤 공백 보존을 검사하지 않았습니다. 이후 해당 손실을 재현해
생성·프로필·송신·MCP 목록 경로를 수정했고, 독립 재현 검사를 포함한 102개 검사가 통과했습니다.

**실제 앱의 Quick CMD 입력 검증·모델 생성, 별도 프로필 저장/재읽기, 송신 인코딩과 플러그인을 통한 명령 재생이 통과했습니다. GUI의 `+ → Save → Quick 버튼 클릭`은 창 소유자 검증 오류로 미검증입니다.**

현재 플러그인의 21개 MCP 도구 중 Quick CMD에 해당하는 것은 `serial_list_commands`뿐입니다.
Quick CMD 생성·수정·삭제 또는 이름으로 버튼을 실행하는 도구는 없습니다.
저장 시퀀스의 save/run/delete는 별도 기능이므로 Quick CMD 제작 성공으로 계산하지 않았습니다.

## 확인한 결과

| 검사 | 결과 |
| --- | --- |
| 실제 `TryNormalizeSavedCommand`로 Terminal/HEX 명령 모델 생성 | PASS — 이름 정리, Ctrl+1/Ctrl+2 단축키 포함 |
| 실제 `ProfileService`로 별도 파일 저장/재읽기 | PASS — 두 명령의 이름·내용·단축키 보존, 저장된 줄 끝으로 인코딩 |
| 빈 이름/내용, 지원하지 않는 단축키 | PASS — 입력 거부 |
| 잘못된 HEX 내용 `GG` 송신 인코딩 | PASS — 오류로 거부 |
| 실제 `TxPayloadEncoder`의 Terminal CRLF | PASS — `PING QUICK-TERM\r\n` 17바이트 |
| 실제 `TxPayloadEncoder`의 HEX | PASS — 모든 줄 끝 선택에서 추가 바이트 없이 원본 19바이트 |
| Quick CMD와 전역 TX 모드 관계 | PASS — 같은 `41 42` 명령이 Terminal에서는 문자열, HEX에서는 `41 42` 두 바이트 |
| 플러그인 경유 Terminal 명령 재생 | PASS — `PONG QUICK-TERM\r\n` 전체 17바이트 일치 |
| 플러그인 경유 HEX 명령 재생 | PASS — 19바이트 binary echo 전체·순번·payload·CRC 일치 |
| 기존 GUI Quick CMD 목록과 표시 모드 | PASS — 검사 전후 목록 동일, Terminal 유지 |
| GUI `+` 대화상자에서 저장/수정/삭제/버튼/단축키 실제 조작 | **미검증 — UI 자동제어의 창 소유자 검증 오류** |

새 `QuickCommandCreationTests` 8개와 기존 `SerialTransmitIntegrityTests` 13개를 합친
**21개 xUnit 테스트가 통과**했습니다. 모델 생성은 실제 앱의 입력 정규화 함수를 호출한 것이며,
창의 Add/Save 이벤트나 `AddSavedCommand` UI 갱신까지 실행한 것으로 간주하지 않습니다.
프로필은 테스트 전용 경로에 저장했습니다. 실행 중인 GUI의 기본 프로필에 이 명령들을 주입하지 않았습니다.

실기 검증은 **5개 검사 묶음, 31회 MCP 호출**입니다. 보드 ID 확인을 포함한 3개 capture의
전체 RX 79바이트를 5페이지로 끝까지 읽었습니다. 초기 미리보기 4바이트 이후의 데이터까지 비교했습니다.
완료 상태는 COM3 / 115200 / 8N1 / Terminal / byte timeout 40ms / busy=false / serialErrors=0,
sessionGeneration=8입니다.

## Terminal / HEX 명령

| 이름 | 내용 | 송신 방식 |
| --- | --- | --- |
| F103RB Quick Terminal | `PING QUICK-TERM` | text + CRLF |
| F103RB Quick HEX | `A55A02921000000800000A0D7F80A55AFF0C89` | hex, 줄 끝 없음 |

HEX 요청은 type=2, sequence=4242, payload=`00 0A 0D 7F 80 A5 5A FF`입니다.
예상 응답과 실제 응답은 `A55A82921000000800000A0D7F80A55AFF1EDD`로 일치했습니다.

**Quick CMD에는 개별 TX 모드 필드가 없습니다.** 현재 전역 TX 모드로 해석됩니다.
따라서 실제 Quick 버튼을 사용할 때 Terminal/HEX 모드를 맞춰야 합니다.
이번 실기 재생에서는 MCP의 명시적 `txMode`를 사용했으며 GUI 표시 모드를 바꾸지 않았습니다.
이는 실제 HEX 화면에서 Quick 버튼을 눌렀다는 증거는 아닙니다.

## 호출 경로와 제한

공식 ModelContextProtocol.Core 2.3.0 클라이언트가 저장소의 실제 `plugins/serial-monitor/mcp.json`
→ 플러그인 런처 → CLI 1.6.2 MCP → 기존 GUI의 COM3 연결로 호출했습니다.
테스트 클라이언트가 COM 포트를 직접 열거나 CLI 송신 동사를 직접 호출하지 않았습니다.
현재 채팅에는 Serial Monitor 도구가 직접 노출되지 않아 Codex 호스트에 설치된 플러그인의
직접 호출 경로까지 검증한 결과는 아닙니다.

최초 자동 탐색은 설치된 구버전 CLI를 골라 discovery에서 실패했습니다. 송신 전의 실패입니다.
성공한 실행은 런처가 지원하는 `SERIAL_MONITOR_CLI`를 테스트 프로세스에만 설정해,
기존의 `release/SerialMonitorPortable/tools/serialctl.exe` 1.6.2를 선택했습니다.
전역 설정이나 설치본은 변경하지 않았습니다.

중간 실행에서 Terminal 요청이 `busy`로 송신 전에 거부되어 해당 실행을 중단했습니다.
이후 읽기 전용 상태 확인으로 유휴 복구를 확인하고, 검사 클라이언트에 송신 전후 유휴 확인을 추가했습니다.
실패한 요청의 송신 여부를 추측해 재전송하지 않았습니다.

컴퓨터 제어는 정확한 COM3 창 ID를 선택했지만 다음 오류로 접근을 거부했습니다.

```text
window id 144578392 no longer belongs to
C:\Users\pjh\AppData\Local\Programs\SerialMonitor\SerialMonitor.WinUI.exe;
current owner is C:\Users\pjh\AppData\Local\Programs\SerialMonitor\SerialMonitor.WinUI.exe
```

따라서 GUI 제작/버튼 테스트는 남아 있습니다. 보호 검사를 우회하거나 GUI 동작을 성공으로 표시하지 않았습니다.

## 증거와 재실행

- 단위 테스트: `../../SerialMonitor.WinUI.Tests/QuickCommandCreationTests.cs`
- 단위 결과: `results/quickcmd-unit/quickcmd-creation.trx`
- 저장 후 다시 읽은 명령: `results/quickcmd-created-cases.json`
- 테스트 전용 프로필 사본: `results/quickcmd-created-profile.json`
- 최종 실기 결과: `results/quickcmd-plugin-20261031-023604-653/report.json`
- 해당 폴더의 `transport.json`, `*-request.json`, `*-result.json`, `*-hex.bin`에 실제 호출과 전체 RX를 보존했습니다.
- 자동 탐색 실패: `results/quickcmd-plugin-20261031-023250-728/`
- busy 거부: `results/quickcmd-plugin-20261031-023403-214/`
- 유휴 복구 확인: `results/quickcmd-busy-status-20261031-023543-549/`

저장소 루트에서 다음과 같이 실행합니다. 실기 검사는 먼저 연결된 유일한 COM3 인스턴스가
115200/8N1이며 유휴 상태인지 확인합니다.

```powershell
$lab = Join-Path $PWD 'firmware\nucleo-f103rb-serial-lab'
$env:SERIAL_LAB_QUICKCMD_CASES = Join-Path $lab 'results\quickcmd-created-cases.json'
dotnet test .\SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~QuickCommandCreationTests|FullyQualifiedName~SerialTransmitIntegrityTests'
# 위 단위 테스트가 성공한 후에만 실기 재생을 실행합니다.
$env:SERIAL_MONITOR_CLI = Join-Path $PWD 'release\SerialMonitorPortable\tools\serialctl.exe'
$evidence = Join-Path $lab ('results\quickcmd-plugin-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
dotnet run --project (Join-Path $lab 'tests\PluginMcpFullCheck\PluginMcpFullCheck.csproj') -c Release -- .\plugins\serial-monitor $evidence quick
```
