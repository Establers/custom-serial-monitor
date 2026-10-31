# 플러그인 경유 MCP 실기 검증 — 2026-10-31

**플러그인의 실제 stdio 런처를 통한 MCP 도구 호출 61회, 실기 검사 13개가 모두 통과했습니다.**
COM3의 UART RX를 MCP 페이지로 끝까지 읽어 순번·payload·CRC를 판정했습니다.

## 검증한 경로와 범위

```text
공식 ModelContextProtocol.Core 2.3.0 클라이언트
  → plugins/serial-monitor/mcp.json
  → scripts/Start-SerialMonitorMcp.ps1 (실제 플러그인 런처)
  → 런처가 자동으로 탐지한 serialctl 1.6.2의 MCP 서버
  → 실행 중인 Serial Monitor GUI의 기존 COM3 연결
  → NUCLEO-F103RB SERIAL-LAB/1 펌웨어
```

클라이언트는 manifest에 적힌 프로그램과 인자를 읽어 실행했습니다.
검사 중 장치 제어는 `CallToolAsync`를 통해서만 수행했습니다.
JSON-RPC를 직접 작성하거나 CLI의 request/mode 동사를 직접 실행해 MCP 호출로 표시하지 않았습니다.
클라이언트는 COM 포트를 열지 않았습니다.

**Codex 호스트 연결 상태는 별개입니다.** 이 채팅의 도구 목록에는 Serial Monitor MCP 도구가
노출되지 않았고, 점검 당시 `codex plugin list --json`의 설치 목록에도 Serial Monitor가 없었습니다.
따라서 이번 검증은 **저장소 플러그인 패키지 자체의 실제 실행 경로**에 대한 결과이며,
Codex에 설치된 플러그인을 이 채팅에서 직접 호출하는 경로까지 통과했다고 주장하지 않습니다.
호스트 설정이나 설치 목록을 바꾸지 않고 저장소 런처를 공식 SDK에 연결했습니다.

사용한 SDK API는 [공식 MCP C# SDK의 클라이언트 예제](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/getting-started.md)를 기준으로 했습니다.

## 결과

| 검사 | 결과 |
| --- | --- |
| manifest stdio 연결 / initialize / tools list / capabilities | PASS, 21개 도구 탐지, CLI 1.6.2 / control API 1.0 |
| 인스턴스 탐색 / 상태 조회 / 보드 식별 | PASS, COM3에 연결한 유일한 GUI를 선택하고 실제 ID 응답 확인 |
| Terminal 모드 CR/LF/CRLF 송수신 | PASS, 각각 `PONG mcp-cr`, `PONG mcp-lf`, `PONG mcp-crlf` |
| Terminal 공백 / 한글 / emoji | PASS, `ECHO   MCP 한글 🚀  `의 UTF-8와 공백 전체 일치 |
| 분할 응답 | PASS, `SPLIT 20 MCP-SPLIT-OK`의 전체 바이트 일치 |
| Terminal 화면 + 명시적 HEX 송신 | PASS, 0x00..0xFF를 모두 포함한 267바이트 프레임의 응답·CRC 전체 일치 |
| Terminal 대량 수신 / 전체 페이지 읽기 | PASS, BURST 100줄의 순번·개수·32바이트 padding 일치 |
| HEX 화면 + 명시적 HEX 송신 | PASS, 전체 binary echo 프레임 일치, 줄 끝 추가 없음 |
| HEX 화면 + 명시적 text 송신 | PASS, `PING text-in-hex` → `PONG text-in-hex` |
| MCP 실시간 byte timeout 변경 | PASS, 1/40/120ms에서 각각 HEX 20프레임 순번·payload·CRC 일치 |
| 잘못된 HEX 입력 | PASS, `GG`를 invalid_argument로 거부하고 같은 MCP 세션의 다음 정상 PING 통과 |
| Terminal 저장 시퀀스 | PASS, `F103RB LAB - CLI smoke` 7/7단계 sent/matched, 전체 RX 추가 검증 |
| HEX 저장 시퀀스 | PASS, `F103RB LAB - Binary CRC echo` 1/1단계 sent/matched, 전체 21바이트 프레임 일치 |

MCP의 `format=hex` 페이지와 GUI의 `mode=hex`는 독립적입니다.
검사에서는 실제 GUI 모드를 Terminal/HEX로 변경하고 상태로 다시 확인했으며,
각 모드에서 `txMode=text`와 `txMode=hex`를 각각 전송했습니다.
텍스트의 출력 형식만 HEX로 바꾼 검사가 아닙니다.

16개 capture의 전체 **13,031바이트**, 총 **25페이지**를 읽었습니다.
미리보기 뒤 `hasMore=true`이면 `nextOffset`으로 이어 읽었고,
전체 수집 바이트 수와 일치하는지 확인했습니다. capture gap·drop·serialErrors는 모두 0입니다.
관찰 창은 기본 600ms, 분할 응답과 burst는 1,500ms, HEX 생성은 700ms였습니다.
시퀀스는 저장된 단계별 captureMs를 적용했습니다.

## 증거

```text
results/plugin-mcp-20261031-020805-894/
```

- `transport.json`: 실제 manifest, 플러그인 경로, 실행 프로그램과 인자
- `tools.json`, `capabilities.json`: MCP에서 조회한 도구 목록과 실시간 스키마
- `NNN-도구명-request.json`, `NNN-도구명-result.json`: 61회 호출의 인자와 원본 결과
- `captureId.bin`, `captureId-capture.json`: MCP 페이지로 재구성한 전체 raw RX와 수집 메타데이터
- `terminal-sequence-definition.json`, `hex-sequence-definition.json`: 실행 전 조회한 기존 시퀀스
- `report.json`: PASS 13개, failure=null, 호출·수집·페이지 수
- `final-status.json`: 종료 시 실제 연결 상태

첫 실행에서는 검사 클라이언트가 compact HEX 페이지를 공백으로 구분한 바이트로 가정해
파싱에 실패했습니다. 저장된 실제 ID 응답 43바이트가 정상임을 확인한 뒤,
표준 HEX 디코더로 검사기를 수정하고 검사 세트를 실행했습니다.
이때 장치 펌웨어나 플러그인 제품 코드는 변경하지 않았으며 실패한 TX를 자동 재시도하지 않았습니다.
첫 증거도 `results/plugin-mcp-20261031-020719-582/`에 남아 있습니다.

## 종료 상태

```text
instanceId: instance-68596-fcdc3427fb894687bede700f33bdb66a
COM3 / 115200 / 8N1 / no handshake / UTF-8
mode: terminal
byteTimeoutMs: 40
connected: true
busy: false
serialErrors: 0
```

기존 시퀀스를 조회·재사용했으며 중복 등록하거나 덮어쓰지 않았습니다.

## 재실행

GUI가 올바른 COM3/115200 설정으로 연결돼 있고 다른 작업이 없을 때,
프로젝트 루트 `C:\Users\pjh\Desktop\serial`에서 실행합니다.

```powershell
dotnet run --project .\firmware\nucleo-f103rb-serial-lab\tests\PluginMcpCheck -- `
  C:\Users\pjh\Desktop\serial\plugins\serial-monitor `
  C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab\results\plugin-mcp-rerun --allow-mode-switch
```

마지막 결과 폴더는 매번 새 이름을 쓰세요. 모드 변경은 보이는 로그/검색을 지우는 앱 동작을
동반하며, 완료하면 최초 모드와 byte timeout으로 복구합니다. 검사기는 펌웨어를 재기록하거나
장치를 리셋하지 않습니다. 현재 장치/기능과 스키마는 MCP 도구에서 직접 조회합니다.
