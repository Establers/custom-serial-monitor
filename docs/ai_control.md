# AI CLI / MCP로 실행 중인 GUI 제어하기

여러 AI에서 같은 절차를 쓰려면 [공통 제어 스킬](agent_skills.md)을 설치할 수 있습니다.
기본은 CLI이며 MCP는 선택입니다.

GUI가 COM 포트를 소유합니다. `serialctl`과 MCP 서버는 같은 Windows 사용자의
Named Pipe로 실행 중인 GUI에 요청하며 COM 포트를 별도로 열지 않습니다.
여러 GUI가 켜져 있으면 `instanceId`로 대상을 선택합니다.

## 시작하기

새 빌드의 GUI를 실행합니다. 이전 버전으로 이미 켜진 GUI에는 제어 서버가 없으므로
기존 세션을 정리하고 새 버전으로 실행해야 합니다. 평소처럼 GUI에서 연결하거나
CLI의 `connect`를 사용합니다. 포터블/설치 배포에는 `tools\serialctl.exe`가 포함됩니다.

```powershell
.\tools\serialctl.exe instances
.\tools\serialctl.exe ports
.\tools\serialctl.exe connect --port COM7 --baud 115200
.\tools\serialctl.exe status
.\tools\serialctl.exe commands
.\tools\serialctl.exe request --port COM7 --text "status" --ending crlf --duration-ms 2000
.\tools\serialctl.exe request --hex "AA 01 FF" --duration-ms 3000
.\tools\serialctl.exe request --text-file command.txt --ending none --duration-ms 2000 --preview-bytes 4096
.\tools\serialctl.exe capture --duration-ms 1000
.\tools\serialctl.exe cancel
```

개발 중에는 `dotnet build SerialMonitor.Cli\SerialMonitor.Cli.csproj -c Debug` 후
`SerialMonitor.Cli\bin\Debug\net8.0\serialctl.exe`를 사용합니다(.NET 8 Runtime 필요).
배포 도구는 self-contained 단일 EXE입니다. 모든 데이터 명령은 JSON을 출력합니다.
여러 GUI가 켜져 있으면 `--instance instance-<pid>-<launch-id>`를 추가합니다.

`connect`는 GUI의 현재 data bits/parity/stop bits/handshake를 사용합니다. 다른 포트나
baud로 이미 연결되어 있으면 교체하지 않고 `already_connected`를 반환합니다. 실제
설정은 `status`에서 확인합니다. send/capture의 선택 인자 `--port`는 현재 연결이 기대한
포트인지 검증하고, 다르면 `port_mismatch`를 반환합니다.

연결 준비 중에는 GUI의 연결 버튼과 연결 설정 편집이 비활성화됩니다. 완료 후 포트·baud가
요청과 다르면 `connection_mismatch`를 반환합니다. 포트 조회·연결 대기 중 요청을 취소하면
뒤늦게 연결을 시작하지 않습니다. 연결 성공 이후에는 요청 종료·취소가 정상 세션을 끊지 않습니다.

`send`는 쓰기 완료만 확인합니다. 응답을 판단하려면 `request`(`send-and-capture` 별칭)를
사용합니다. 명령 없는 추가 관찰은 `capture`입니다. text TX는 UTF-8이고 공백을 보존합니다.
HEX TX는 지정 바이트만 보내며 줄 끝을 붙이지 않습니다.

`--text-file`은 UTF-8 명령 파일을 데이터로 읽어 따옴표·줄바꿈을 그대로 보존합니다.
파일에 줄 끝이 있으면 `--ending none`으로 중복 추가를 피하세요. `--preview-bytes`는
첫 반환 페이지만 4..65536바이트로 제한하며 전체 수집과 파일 저장은 유지합니다.
MCP의 대응 인자는 `previewBytes`입니다. 생략 시 기존 기본값 16384를 사용합니다.

## 전체 RX와 페이지 조회

`request`는 TX 전에 독립적인 RX 구독을 등록하고, 전송 중부터 전송 완료 후 지정 시간까지의
**모든 RX**를 수집합니다. 매칭·성공 키워드 필터를 사용하지 않습니다. 화면 일시정지·필터·
Terminal/HEX 모드와 독립적입니다. 제품에서 섞인 `abc1d234`도 실제 수신 순서로 저장합니다.

응답의 `capture`에는 구간 ID, 포트, 세션, 시각, 요청 수집 시간과 다음 정보가 포함됩니다.

- `sendStatus`: `not_requested`, `not_sent`, `sent`, `unknown`
- `captureStatus`: `completed`, `canceled`, `disconnected`, `rx_gap`, `size_limit`,
  `write_error`, `send_error` 등의 수집 상태
- `receivedBytes`, `storedBytes`, `droppedBytes`, `serialErrors`, `rxGapDetected`
- byte-exact `.bin`과 디코딩한 UTF-8 `.txt` 파일 경로

`sent`는 앱의 쓰기 완료이며 제품의 실행 성공/ACK를 보장하지 않습니다. `completed`도 수집이
끝났다는 뜻입니다. **제품 정상/실패는 AI가 실제 RX를 읽고 판단합니다.** 실패·취소 시 일부
TX가 이미 장치에 도달했을 수 있어 자동 재시도하지 않습니다.

`send`, `request`, `capture`의 수집 결과가 `completed`가 아니면 `success=false`와
`error.code`로 해당 상태를 반환하며, MCP도 `isError=true`로 표시합니다. 이때도
`data.capture`와 가능한 `data.page`를 보존하므로 `sendStatus`, `captureId`, 파일 경로와
부분 RX를 확인할 수 있습니다. `read_capture`는 저장된 구간을 읽는 작업이므로, 과거 수집이
실패했더라도 페이지 읽기 자체가 성공하면 `success=true`입니다.

`page`는 기본 16 KiB 텍스트입니다. `hasMore=true`이면 나머지 원문이 파일에 있습니다.
조용히 잘리거나 요약된 것이 아닙니다. `nextOffset`으로 다음 페이지를 읽습니다.

```powershell
.\tools\serialctl.exe read --capture CAPTURE_ID --offset 16384 --limit 16384
.\tools\serialctl.exe read --capture CAPTURE_ID --offset 0 --limit 16384 --format hex
```

offset은 해당 파일의 바이트 위치입니다. UTF-8 문자 경계에 맞춰 페이지가 끝나므로 반드시
반환된 `nextOffset`을 사용하세요. text와 hex의 offset 공간은 다릅니다. hex는 원본 바이트의
16진수 표현입니다. 텍스트는 수집 시작 당시 GUI의 RX encoding으로 디코딩합니다. 구간 시작에
이미 전송 중인 문자의 일부만 들어오면 대체 문자가 생길 수 있지만 원본 바이트는 보존됩니다.
TX는 RX 원문에 포함되지 않습니다.

## MCP 등록

앱의 **MCP** 탭에서도 이 가이드를 확인할 수 있습니다. 왼쪽 목차(좁은 창은 상단 선택)에서
빠른 시작, 클라이언트 설치, 사용 예시, 한계·문제 해결을 선택합니다. 빠른 시작에서
AI 도구와 `serialctl.exe` 경로를 선택하고, 등록 설정 → 연결 테스트 → 1초 RX 수집 순서로
따라갈 수 있습니다. 연결 테스트는 COM 포트 미연결 상태에서도 가능하며, RX 수집은
GUI에서 장치를 연결한 뒤 진행합니다. 클라이언트 설치에는 범위와 대체 등록 방법을 안내합니다.
이 탭은 안내와 클립보드 복사만 제공하며 클라이언트 설정 파일을 자동 수정하지 않습니다.

### CLI와 VS Code 확장별 설정

| 클라이언트 | 등록 위치 / 방법 | 확인 방법 |
| --- | --- | --- |
| Claude Code CLI | `claude mcp add --transport stdio --scope user` / 프로젝트는 `.mcp.json`의 `mcpServers` | `claude mcp list`, 대화의 `/mcp` |
| Codex CLI | `codex mcp add` / `~/.codex/config.toml`의 `[mcp_servers.serial-monitor]` | `codex mcp list`, 대화의 `/mcp` |
| GitHub Copilot CLI | `copilot mcp add` 또는 대화의 `/mcp add` / `~/.copilot/mcp-config.json`의 `mcpServers` | 대화의 `/mcp` |
| VS Code Claude Code | 같은 호스트의 Claude Code 설정 공유. 확장의 `/mcp` 또는 통합 터미널 등록 | 새 대화에서 `/mcp` |
| VS Code Codex | 같은 호스트의 Codex 설정 공유. 확장의 MCP servers 메뉴에서도 등록 | 저장 후 확장 재시작 |
| VS Code GitHub Copilot | `.vscode/mcp.json`의 `servers`, `type: stdio` | `MCP: List Servers`, MCP 도구를 사용하는 Agent 세션 |

기존 JSON의 서버 항목이나 TOML 테이블에 합치고 다른 설정은 보존하세요. Claude Code의
user/local/project 범위는 구분됩니다. `CODEX_HOME`이나 `COPILOT_HOME`을 지정했다면
해당 설정 위치를 사용합니다. VS Code Copilot의 설정은 Claude Code·Codex 확장의 전용
설정과 별개입니다. 클라이언트 메뉴는 버전에 따라 달라질 수 있습니다.

EXE 경로를 실제 경로로 바꿉니다. 사용자 계정의 AI CLI 설정은 자동 변경하지 않습니다.

```powershell
codex mcp add serial-monitor -- "C:\SerialMonitor\tools\serialctl.exe" mcp
claude mcp add --transport stdio serial-monitor -- "C:\SerialMonitor\tools\serialctl.exe" mcp
copilot mcp add serial-monitor -- "C:\SerialMonitor\tools\serialctl.exe" mcp
```

**Codex CLI / VS Code Codex는 등록 명령 실행 후 제한 시간 설정까지 필요합니다.**
`%USERPROFILE%\.codex\config.toml`(또는 `CODEX_HOME` 아래의 `config.toml`)을 열고,
기존 `[mcp_servers.serial-monitor]` 테이블 안에서 다음 값을 추가하거나 수정합니다.
다음 테이블이 시작되기 전에 넣고, 같은 이름의 테이블을 중복 생성하지 않습니다.

```toml
tool_timeout_sec = 90
```

등록 명령만 실행하면 기본 60초 제한이 유지되어 최대 60초 캡처와 충돌할 수 있습니다.
90초로 설정하고 Codex 대화 또는 확장을 재시작합니다. 앱 MCP 탭의 전체 TOML 예제로
직접 등록하는 경우에는 이 설정이 이미 포함되어 있습니다.

서버는 stdio를 사용하고 stdout에는 MCP JSON-RPC만 출력합니다. 도구는 다음과 같습니다.

| 도구 | 역할 |
| --- | --- |
| `serial_list_instances` | 실행 중인 GUI 조회 |
| `serial_get_status`, `serial_list_ports` | 연결·설정·오류와 포트 조회 |
| `serial_list_commands` | GUI의 저장 명령과 줄 끝 조회 |
| `serial_connect`, `serial_disconnect` | GUI 연결·해제 |
| `serial_send` | 명령 한 번 전송 |
| `serial_send_and_capture` | 전송하고 지정 시간의 전체 RX 수집 |
| `serial_capture_rx` | 전송 없이 추가 RX 수집 |
| `serial_read_capture` | 저장된 RX 페이지 조회 |
| `serial_cancel_capture` | 현재 수집 중지 |

`serial_list_instances` 이외에는 선택적으로 `instanceId`를 지정합니다. send/capture에는
기대 연결을 검증하는 `port`도 지정할 수 있습니다. 그 외 인자는 도구의 JSON Schema에
설명됩니다. 명령 목록은 GUI의 저장 명령에서 가져옵니다. 다른 명령은 제품 명령 문서를 AI에
제공해야 합니다. 서버 자체가 제품 명령을 자동 발견하거나 자체 LLM을 실행하지는 않습니다.

AI 요청 예:

> Serial Monitor의 연결과 저장 명령을 확인해줘. COM7에 status를 CRLF로 보내고 2초간의
> 모든 RX를 읽어줘. 출력이 섞일 수 있으니 매칭으로 단정하지 말고 내용을 판단해줘.
> page.hasMore이면 필요한 페이지를 계속 읽고, 누락이나 연결 오류도 확인해줘.
> 실제 RX를 보고 필요한 다음 명령을 선택해줘.

공식 등록 문서: [Codex](https://learn.chatgpt.com/docs/extend/mcp?surface=cli),
[Claude Code](https://code.claude.com/docs/en/mcp),
[Claude Code VS Code](https://code.claude.com/docs/en/vs-code#connect-to-external-tools-with-mcp),
[GitHub Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-mcp-servers),
[VS Code MCP 설정](https://code.visualstudio.com/docs/agents/reference/mcp-configuration).
공식 설정 문서 확인일: 2026-10-25. 앱의 수집 한도·기능은 소스 구현 기준입니다.

## 동시 제어·한도

- GUI당 제어 작업 하나입니다. GUI TX·시퀀스·Bridge 시작 및 실행·재연결·연결 변경 중 외부 변경 요청은
  `busy`입니다. AI 수집 중 GUI TX·시퀀스·Bridge 시작도 차단됩니다. 관찰·검색은 유지됩니다.
- 하단 `AI 중지`, CLI `cancel`, MCP 취소로 수집을 끝낼 수 있습니다. GUI 직접 연결 해제도
  수집을 끝냅니다. CLI/MCP가 종료되어 pipe가 닫히면 작업을 취소하고 제어를 반환합니다.
  GUI는 계속 실행되며 이미 보낸 명령을 되돌리지 않습니다.
- 구간 최대 60초, 원본 최대 64 MiB, 큐 최대 1,024개 chunk 및 8 MiB입니다. 초과하면
  수집을 중단하고 누락/제한을 명시하며 시리얼 수신을 막지 않습니다.
- 페이지 기본 16 KiB, 최대 64 KiB입니다. 모든 페이지를 읽으면 AI 처리 총량은 줄지 않으므로
  명령별로 적절한 수집 시간을 지정합니다.
- 실행별 최대 32개 구간을 보관합니다. 다음 구간 시작 시 오래된 파일부터 512 MiB 기준으로
  정리하고, 진행 중 구간은 그 크기만큼 추가될 수 있습니다. 종료된 실행 폴더는 증거 보존을
  위해 남으며, GUI 종료 후에도 반환된 파일 경로로 직접 열 수 있습니다.
- 페이지 조회나 외부 뷰어에서 사용 중인 캡처는 메타데이터와 원문을 함께 보존하고 다른 정리
  대상을 선택합니다. 모두 사용 중이면 한도를 일시적으로 넘을 수 있으며, 파일 사용이 끝난 뒤
  다음 수집에서 다시 정리합니다. 잠긴 과거 파일 때문에 새 수집을 실패시키지 않습니다.
- 위치: `%LOCALAPPDATA%\SerialMonitor\automation\captures\instance-*`.
  파일·큐 오류는 결과에, 제어 서버 실패는 GUI 상태와 런타임 진단에 표시됩니다.
- 같은 Windows 사용자로 실행한 로컬 CLI용입니다. WSL/컨테이너에서 직접 Windows
  Named Pipe에 접속하는 경로는 제공하지 않습니다.

## 검증 범위

테스트는 즉시 응답, 섞인 출력, UTF-8 분할·페이지 경계, 대용량, 취소, 세션 변경, 과부하,
실패 TX 무재시도, Named Pipe 왕복, MCP 초기화·호출·취소를 다룹니다. WinUI 테스트는
Bridge 우선 모드 여부와 무관한 RX 수집과 기존 파서 채널 보존을 검증합니다. 실제 제품과
USB-UART의 동작은 해당 하드웨어를 연결해 별도로 확인해야 합니다.
