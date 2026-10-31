# 플러그인 전체 도구 검증 — 2026-10-31

**실시간으로 발견한 21개 MCP 도구 모두 정상 호출과 오류 거부를 확인했습니다.**
최종 검증은 30개 검사 묶음, 238회 MCP 도구 호출이며, 초기 진단·중간 재현 실행은 이 숫자에서 제외했습니다.
MCP 호출에는 정상 동작뿐 아니라 예상한 오류/거부를 확인하기 위한 호출도 포함됩니다.

검사 경로는 저장소의 실제 `plugins/serial-monitor/mcp.json` → 플러그인 런처 → CLI 1.6.2 MCP
→ 실행 중인 GUI → COM3의 NUCLEO-F103RB입니다. 공식 MCP C# SDK 2.3.0으로 도구를 호출했습니다.
검사 클라이언트가 COM 포트를 직접 열거나 CLI 전송 동사를 직접 실행한 결과가 아닙니다.

## 도구별 증거

정상/거부 열은 해당 종류의 MCP 응답 수입니다. 클라이언트 토큰으로 취소해 응답을 받지 않은 요청,
존재하지 않는 도구 테스트, 별도 클라이언트의 background start는 합계 238회에는 포함되지만
아래 주 클라이언트 도구별 응답 수와는 별도로 기록됩니다.

| MCP 도구 | 정상 | 예상 거부/취소 | 결과 |
| --- | ---: | ---: | --- |
| serial_get_capabilities | 2 | 1 | PASS |
| serial_list_instances | 2 | 1 | PASS |
| serial_get_status | 27 | 1 | PASS |
| serial_list_ports | 1 | 1 | PASS |
| serial_list_commands | 6 | 1 | PASS |
| serial_connect | 2 | 2 | PASS |
| serial_disconnect | 1 | 3 | PASS |
| serial_set_mode | 3 | 2 | PASS |
| serial_configure | 7 | 9 | PASS |
| serial_send | 2 | 3 | PASS |
| serial_send_and_capture | 31 | 11 | PASS |
| serial_capture_rx | 4 | 2 | PASS |
| serial_read_capture | 41 | 6 | PASS |
| serial_cancel_capture | 2 | 2 | PASS |
| serial_sequence_list | 14 | 1 | PASS |
| serial_sequence_get | 1 | 3 | PASS |
| serial_sequence_save | 3 | 6 | PASS |
| serial_sequence_delete | 2 | 3 | PASS |
| serial_sequence_start | 1 | 3 | PASS |
| serial_sequence_status | 18 | 1 | PASS |
| serial_sequence_cancel | 2 | 1 | PASS |

집계는 `scripts/summarize-plugin-check.ps1`로 수행했습니다. 발견한 도구 중 정상 또는 거부 증거가 하나라도 빠지면 완료로 처리하지 않습니다.

## 실행한 내용

- 목록: 실제 포트, 저장 명령, 저장 시퀀스를 페이지 크기 1로 끝까지 읽고 마지막 커서도 확인했습니다.
- 연결: 동일 연결의 재사용, 실제 disconnect/connect, 새 수신 세션, 미연결 상태의 전송·설정 거부를 확인했습니다.
- 모드: 검사 전에 알린 뒤 Terminal → HEX → Terminal 두 번만 실제 전환했습니다. 각 모드에서 text TX와 HEX TX를 모두 시험했습니다.
- 설정: byte timeout 1/5000ms 경계와 복구, 장치 ACK에 맞춘 PC/DUT baud 115200 → 230400 → 115200 전환을 확인했습니다.
- 송신: send와 send-and-capture를 각각 text/HEX로 호출했습니다. send 뒤 RX-only capture로 지연 응답을 검증했습니다.
- 텍스트: CR/LF/CRLF/None, UTF-8 한글·emoji, 공백·따옴표·백슬래시·리터럴 문자열을 전체 바이트로 비교했습니다.
- 바이너리: 0x00..0xFF가 포함된 프레임의 순번·payload·CRC를 확인했습니다.
- 수집/읽기: 기본 preview 형식, 4바이트 UTF-8 페이지, 기본 형식 유지와 HEX 재조회, 전체 RX, 150줄 순번/padding을 확인했습니다.
- 경계: durationMs=0, previewBytes=65536, durationMs=60000 요청 수락 후 취소를 확인했습니다. 60초 상한 수락 검사는 60초 내구 시험과 구분합니다.
- 수집 취소: 수집 중 6종 제어의 busy 거부, 명시적 취소, 부분 RX 보존, idle cancel=false, 이후 정상 통신을 확인했습니다.
- 시퀀스: 새 저장의 persisted, 저장만으로 TX가 발생하지 않음, 중복 거부, get, 테스트용 정의만 명시적으로 overwrite, 2회 반복 실행을 확인했습니다.
- 예상 미일치: SILENT의 not_matched 뒤 다음 단계가 계속 실행됐고, 장치 명령 카운터로 자동 재전송이 없음을 확인했습니다.
- 백그라운드: 별도 MCP 클라이언트가 start 후 종료돼도 GUI 시퀀스가 계속 실행되는 것을 확인했습니다.
- 시퀀스 취소: 실행 중 일반 capture cancel 거부, 정확한 runId로 취소, canceled 상태와 보존한 RX를 확인했습니다.
- 삭제: 이번 검사에서 만든 시퀀스만 삭제하고, 기존 시퀀스의 전후 메타데이터가 동일함을 비교했습니다.
- 오류: 잘못된 형식·타입·범위·철자·포트·인스턴스·runId·captureId, GUI import JSON, HEX ?? 기대값, 반복/대기 상한을 거부했습니다.
- 오류 후 복구: 존재하지 않는 도구를 포함한 오류 검사 뒤 같은 MCP 세션의 정상 PING을 확인했습니다. 장치 카운터로 거부된 요청의 TX가 없음을 확인했습니다.

## 발견한 SDK 취소 전파 주의점

공식 SDK의 `CancellationToken`을 취소하면 클라이언트의 대기는 끝났지만,
이번 조합에서는 SDK wire 로그에 자동 `notifications/cancelled` 송신이 없었고 GUI 수집은 계속됐습니다.
이를 플러그인 취소 기능의 성공으로 처리하지 않았습니다.

같은 요청의 실제 SDK request ID를 로그에서 확인해 SDK의 typed `SendNotificationAsync`로
`notifications/cancelled`를 명시적으로 보내면 GUI 수집이 정상 종료됐습니다.
`serial_cancel_capture` 도구도 정상 종료를 확인했습니다.
따라서 클라이언트는 로컬 대기 취소와 장치 작업 취소를 구분해야 합니다.
GUI 수집을 끝내려면 명시적 취소 도구 또는 실제 MCP 취소 알림을 사용하고 idle 상태를 확인하세요.
이미 보낸 TX는 취소로 되돌아가지 않습니다.

이번 결과만으로 SDK 내부 원인이나 다른 클라이언트의 자동 취소 동작을 단정하지 않습니다.
SDK가 설계한 취소 알림 경로는 [버전 2.3.0의 공식 소스](https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.3.0/src/ModelContextProtocol.Core/McpSessionHandler.cs)를 참고했습니다.
실제 관찰 증거는 아래 SDK 로그와 `sdk-token-cancel-observation.json`에 보존했습니다.

## 최종 증거

| 경로 | 내용 |
| --- | --- |
| `results/plugin-full-safe-20261031-022348-342/` | 모드 유지 검사 25개, 주 클라이언트 214회 호출, 별도 background client 호출 |
| `results/plugin-full-state-20261031-022300-800/` | 실제 연결/모드 변경 검사 6개, 23회 호출 |
| `results/all-plugin-summary.json` | 21개 도구의 정상·거부 합산과 최종 상태 |
| 각 폴더의 `NNNN-*-request.json`, `NNNN-*-result.json` | 모든 도구 인자와 원본 응답 |
| 각 폴더의 `sdk-wire.log` | SDK가 실제 송수신한 프로토콜 로그 |
| 각 폴더의 `captureId-*.bin` | MCP 페이지로 끝까지 읽은 RX |
| safe 폴더의 `sequence-snapshot-before/after.json` | 기존 시퀀스 보존 비교 |

앞선 중간 실행도 보존했습니다. 검증기에서 명시적 HEX 재조회 후 다음 페이지의 format을
생략한 오류는 수정했고, 토큰 취소와 명시적 취소를 혼동한 기대 조건도 분리했습니다.
최종 두 실행의 report.failure는 모두 null이고, 남은 테스트용 시퀀스는 없습니다.

## 종료 상태

```text
COM3 / 115200 / 8N1 / no handshake / UTF-8
mode=terminal, byteTimeoutMs=40
connected=true, busy=false, serialErrors=0
```

모드 검사 전 사용자에게 화면 초기화를 알렸고, 이후 최종 safe 실행에서는 화면 모드를 바꾸지 않았습니다.
펌웨어를 다시 기록하거나 장치를 리셋하지 않았습니다.

검증 범위는 실제 저장소 플러그인 런처의 21개 MCP 도구입니다.
Codex 채팅의 native 도구 노출/설치 상태, Claude/Copilot 등 다른 호스트의 설치 동작,
모든 가능한 잘못된 JSON 조합이나 수일간 내구 시험까지 포함한다는 의미는 아닙니다.

## 재실행

프로젝트 루트에서, 실제 플러그인 경로와 **새 결과 폴더**를 지정합니다.

```powershell
dotnet run --project .\firmware\nucleo-f103rb-serial-lab\tests\PluginMcpFullCheck -- `
  C:\Users\pjh\Desktop\serial\plugins\serial-monitor `
  C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab\results\new-safe safe

# 아래 state 검사는 실제 연결을 끊고 재연결하며 화면 모드를 두 번 바꿉니다.
# 실행 전에 화면 로그와 검색이 초기화될 수 있음을 확인하세요.
dotnet run --project .\firmware\nucleo-f103rb-serial-lab\tests\PluginMcpFullCheck -- `
  C:\Users\pjh\Desktop\serial\plugins\serial-monitor `
  C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab\results\new-state state
```

`scripts/summarize-plugin-check.ps1 -SafeEvidence <safe 폴더> -StateEvidence <state 폴더>`는
두 실행 성공, 모든 도구의 정상/거부 증거, 테스트용 시퀀스 정리, 최종 연결 복구를 확인합니다.
