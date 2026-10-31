# AI Plugin / CLI / MCP로 실행 중인 GUI 제어하기

새 설치는 skill과 로컬 MCP 연결을 함께 제공하는
[Serial Monitor Plugin](https://github.com/Establers/custom-serial-monitor/blob/main/docs/agent_plugins.md)을 사용합니다. GUI의 **Plugin** 탭에서
Codex·Claude Code·Copilot별 설치·업데이트 명령을 복사할 수 있습니다.
플러그인 지원이 없는 환경에서는 [단독 skill](https://github.com/Establers/custom-serial-monitor/blob/main/docs/agent_skills.md)이나 아래 직접 CLI/MCP
경로를 사용할 수 있습니다. 실제 연결된 MCP 도구가 있으면 해당 도구를 우선 활용합니다.

GUI가 COM 포트를 소유합니다. `serialctl`과 MCP 서버는 같은 Windows 사용자의
Named Pipe로 실행 중인 GUI에 요청하며 COM 포트를 별도로 열지 않습니다.
여러 GUI가 켜져 있으면 `instanceId`로 대상을 선택합니다.
알 수 없는 인자·중복 인자·잘못된 자료형과 명령에 맞지 않는 CLI 옵션은 실행 전에 거절합니다.
`instanceId:null`은 유효하지 않으며 MCP 시작 시 지정한 대상을 해제하지 않습니다.
`--`로 시작하는 명령 문자열은 `--text=VALUE` 또는 `--text-file`로 전달합니다.

## 시작하기

프로그램 v1.6.0 이상에서는 `serialctl describe --json` 또는 플러그인의
`serial_get_capabilities`로 CLI/server 버전, 제어 API 버전, 최소 플러그인 버전,
지원 기능과 도구 스키마를 조회합니다. 이 조회는 GUI나 COM 포트에 접근하지 않습니다.
실행 중인 GUI 자체가 구버전인지는 별도로 확인해야 합니다. 이전 CLI가 `describe`를
지원하지 않으면 `help`에 있는 기존 기능만 사용하세요.

새 빌드의 GUI를 실행합니다. 이전 버전으로 이미 켜진 GUI에는 제어 서버가 없으므로
기존 세션을 정리하고 새 버전으로 실행해야 합니다. 평소처럼 GUI에서 연결하거나
CLI의 `connect`를 사용합니다. 포터블/설치 배포에는 `tools\serialctl.exe`가 포함됩니다.

```powershell
.\tools\serialctl.exe instances
.\tools\serialctl.exe ports
.\tools\serialctl.exe connect --port COM7 --baud 115200
.\tools\serialctl.exe configure --port COM7 --baud 57600 --byte-timeout-ms 80
.\tools\serialctl.exe status
.\tools\serialctl.exe mode
.\tools\serialctl.exe mode --set hex
.\tools\serialctl.exe mode --set terminal
.\tools\serialctl.exe commands
.\tools\serialctl.exe request --port COM7 --text "status" --ending crlf --duration-ms 2000
.\tools\serialctl.exe request --hex "AA 01 FF" --duration-ms 3000 --format hex
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
`serial_list_commands` / `commands`의 `txMode`와 `lineEnding`은 조회 당시 GUI에서
그 저장 명령을 보낼 때의 설정입니다. HEX이면 `txMode: "hex"`, `lineEnding: "none"`을
반환하므로 재사용할 때 두 필드를 함께 전달하세요.

`--text-file`은 UTF-8 명령 파일을 데이터로 읽어 따옴표·줄바꿈을 그대로 보존합니다.
UTF-8 BOM은 허용하지만 UTF-16/32 파일과 잘못된 인코딩 입력은 전송 전에 거부합니다.
파일에 줄 끝이 있으면 `--ending none`으로 중복 추가를 피하세요. `--preview-bytes`는
첫 반환 페이지만 4..65536바이트로 제한하며 전체 수집과 파일 저장은 유지합니다.
MCP의 대응 인자는 `previewBytes`입니다. 생략 시 기존 기본값 16384를 사용합니다.
`send`·`request`·`capture`에는 `--format text|hex`로 첫 페이지 형식을 지정할 수 있습니다.
HEX TX는 기본적으로 HEX 페이지를 반환하고, 나머지는 text가 기본입니다.

## 연결 중 BPS / byte timeout 변경

제품이 동작 중 BPS를 바꾸는 경우 같은 연결의 호스트 baud rate를 변경할 수 있습니다.
`configure`는 COM 포트를 다시 열지 않고 현재 핸들에 적용하며 장치로 명령을 보내지 않습니다.
장비 자체의 BPS 변경은 제품 명령 규격에 따라 별도로 수행하고, 양쪽 전환 순서를 맞추세요.

```powershell
.\tools\serialctl.exe configure --port COM7 --baud 57600 --instance INSTANCE_ID
.\tools\serialctl.exe configure --port COM7 --byte-timeout-ms 80 --instance INSTANCE_ID
.\tools\serialctl.exe configure --port COM7 --baud 115200 --byte-timeout-ms 40 --instance INSTANCE_ID
.\tools\serialctl.exe status --instance INSTANCE_ID
```

`--baud`는 1..10,000,000 BPS이며 사용자 지정 속도도 허용하지만 시리얼 드라이버가
지원해야 적용됩니다. `--byte-timeout-ms`는 1..5,000ms이고 기본값은 40ms입니다.
둘 중 하나 이상을 지정해야 합니다. 연결된 상태에서만 사용할 수 있으며 연결·재연결을
자동으로 시작하지 않습니다. 연결 전 설정은 GUI 또는 프로필에서 변경합니다.
선택 인자 `--port`는 기대 포트가 맞는지 검증하며 다른 포트로 바꾸는 기능이 아닙니다.
현재 적용값은 `status`의 `baudRate`와 `byteTimeoutMs`로 확인합니다. 수집·송신·시퀀스·
Bridge·다른 제어 작업 중에는 `busy`이므로 작업 종료 후 변경합니다.

byte timeout은 HEX 화면의 유휴 간격별 묶음 기준입니다. 제품 응답 전체를 기다리는
`--duration-ms` / 시퀀스의 `captureMs`와 별개입니다. Windows 스케줄링, USB 어댑터
버퍼링, RS485 전환·처리 시간 때문에 이론적인 바이트 간격보다 넉넉한 실측 여유가
필요합니다. 응답 하나가 여러 줄로 나뉘면 timeout을 늘리고, 서로 다른 교환이 합쳐지면
실제 간격을 관찰하며 줄이세요. 어떤 값도 프로토콜 패킷 경계를 보장하지 않습니다.
정확한 판별에는 제품의 헤더·길이·체크섬/CRC 규격을 사용해야 합니다.

원본 RX 캡처는 화면의 묶음과 관계없이 수신 순서대로 저장되며, 기대 응답 판정도
수신 chunk 경계와 관계없는 연속 바이트를 확인합니다. 한 패킷이 여러 번에 나뉘거나
여러 패킷이 한 번에 도착해도 캡처와 판정을 유지합니다. 응답이 수집 시간 밖으로
늦게 오는 경우에는 `durationMs` / `captureMs`도 충분히 늘려야 합니다.

MCP에서는 `serial_configure`에
`{"port":"COM7","baudRate":57600,"byteTimeoutMs":80,"instanceId":"INSTANCE_ID"}`를
전달합니다. 범위와 동작은 CLI와 같습니다.

## Terminal / HEX 모드 제어

GUI의 PORT 왼쪽 드롭다운과 같은 모드를 CLI/MCP에서도 선택할 수 있습니다.
Terminal은 수신 데이터를 텍스트 터미널로 표시하고, HEX는 원본 바이트를 16진수로 표시합니다.
드롭다운의 각 항목에 마우스를 올리면 한 줄 설명이 나옵니다.

```powershell
.\tools\serialctl.exe mode --instance INSTANCE_ID
.\tools\serialctl.exe mode --set hex --instance INSTANCE_ID
.\tools\serialctl.exe mode --set terminal --instance INSTANCE_ID
```

`mode`는 현재 상태를 조회하며 `status`와 같은 `mode` 필드에 `terminal` 또는 `hex`가
반환됩니다. `--set`은 GUI의 실시간 RX 표시, 수동·시퀀스 TX 입력 해석과 활성 규칙 모드를
함께 바꿉니다. 실제로 모드를 전환하면 현재 화면 로그와 검색 결과가 지워집니다. 이미 저장한
로그와 원본 RX 캡처는 유지되며, 모드 선택만으로 연결하거나 장치에 데이터를 보내지 않습니다.

GUI 모드와 요청별 전송 형식은 독립적입니다. GUI가 HEX여도 `request --text`는 텍스트를,
Terminal이어도 `request --hex`는 지정 바이트를 보냅니다. 캡처의 `read --format hex`도
페이지의 출력 형식만 선택하므로 GUI 모드를 바꾸지 않습니다.

MCP에서는 `serial_get_status`로 조회하고 `serial_set_mode`에
`{"mode":"hex","instanceId":"INSTANCE_ID"}` 또는 `mode: "terminal"`을 전달합니다.
모드 변경은 다른 제어 작업과 동시에 실행되지 않습니다. GUI TX·시퀀스·Bridge 시작/실행·
재연결·AI 제어/수집 중에는 `busy`를 반환하므로 현재 작업이 끝난 뒤 변경합니다.
요청 취소도 지원합니다. 이 명령은 모드 제어를 지원하는 최신 GUI·CLI 빌드에서 사용합니다.

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

`page`의 기본 크기는 16 KiB이며 HEX TX는 hex, 나머지는 text 형식입니다.
`--format` / MCP `format`으로 형식을 명시할 수 있습니다.
이후 `read`에서 형식을 생략하면 최초 캡처의 preview 형식을 유지합니다.
형식을 바꿀 때는 offset 0부터 시작하고 이후 페이지에도 바꾼 형식을 명시하세요.
`hasMore=true`이면 나머지 원문이 파일에 있습니다.
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

GUI 결과 표시나 제어 해제에 오류가 발생해도 이미 끝난 TX의 `sendStatus`, `captureId`,
파일 경로와 가능한 페이지는 오류 응답의 `data`에 보존됩니다. 실패 응답만 보고 재전송하지 마세요.

`commands` / `serial_list_commands`도 `offset`, `limit`(1..100)으로 조회합니다.
직렬화된 페이지 크기도 제한하므로 요청한 limit보다 적게 반환될 수 있습니다.
`hasMore=true`이면 `nextOffset`으로 계속 조회하며 명령 내용은 자르지 않습니다.
이름 128자 또는 명령 16384자를 넘는 기존 항목은 GUI에서 나누거나 수정해야 합니다.

MCP는 한 줄 128 Ki 문자를 넘는 요청을 오류로 반환한 뒤 다음 줄을 처리합니다.
오류 줄을 버리는 작업은 추가 읽기 5초·전체 4 Mi 문자로 제한하며 한도를 넘으면 연결을 닫습니다.
stdout 응답 출력이 5초 안에 완료되지 않으면 손상된 응답 뒤에 다른 응답을 붙이지 않고 세션을 종료합니다.
일반 CLI 출력도 5초로 제한합니다. 출력 실패 시 stderr에 알리고 종료 코드 1을 반환하며,
부분 JSON 뒤에 오류 JSON을 추가하거나 TX를 재실행하지 않습니다. 호출 측은 stdout을 실행 중에
계속 읽거나 파일로 리디렉션해야 합니다.
GUI는 일반 연결 최대 64개와 일반 작업 실행 최대 8개를 별도로 제한합니다. 입력을 보내지 않는
연결과 응답을 늦게 읽는 연결은 실행 슬롯을 점유하지 않습니다. 입력 완성과 응답 쓰기는 각각
5초로 제한하며, 실행 슬롯이 가득 차면 요청을 나중에 실행하도록 쌓지 않고 `busy`를 반환합니다.
최신 GUI는 discovery의 선택 필드 `controlPipeName`으로 취소 전용 파이프를 제공합니다.
최신 CLI/MCP는 `cancel_capture`와 `sequence_cancel`만 이 경로로 보냅니다. 일반 연결 64개가
모두 느린 입력·출력에 점유되어도 취소 연결은 독립적으로 수용합니다. 전용 연결은 최대 4개,
입력 완성과 응답 쓰기는 각각 1초이며, 양쪽 파이프가 취소 실행 슬롯 1개를 공유합니다.
전용 파이프의 TX·상태 조회 등 다른 명령은 실행하지 않고 거절합니다. 두 파이프를 모두
만든 뒤 연결 정보를 게시하고, 종료 시 양쪽 연결과 정보를 함께 정리합니다.
종료 시작 시 discovery 정보를 먼저 제거하고 일반·취소 작업의 신규 수용을 차단합니다.
취소 콜백이나 핸들러가 지연되어도 discovery 제거를 기다리게 하지 않으며, 살아 있는
작업의 자원 정리는 완료 뒤 수행합니다. 시작과 종료가 겹쳐도 임시 파일의 원자적 게시를
종료 상태와 함께 확인하므로 종료된 호스트가 다시 discovery에 나타나지 않습니다.
MCP stdio 세션도 일반 호출 8개와 취소 호출 1개의 수용 한도를 분리하므로 같은 세션의
일반 호출 8개가 GUI 응답을 기다리는 동안 명시적인 취소 도구를 호출할 수 있습니다.
MCP 자체 수용 한도를 넘는 호출은 기존 JSON-RPC 오류 `-32600`으로 거절하고 GUI로 보내지 않습니다.
도구 이름·인자 형식은 수용 한도 검사 전에 검증합니다. 잘못된 요청은 포화 여부와 관계없이
프로토콜 인자 오류 `-32602` 또는 기존 도구 응답의 `invalid_argument`를 유지하며 실행 슬롯을
점유하거나 GUI를 선택하지 않습니다.
지속 연결된 stdin의 동기 대기는 프로세스당 전용 배경 스레드 하나와 2개 chunk의 제한 큐로
분리합니다. 작업 스레드를 stdin 대기로 점유하지 않아 GUI 연결 재시도와 응답 처리를 막지 않습니다.
UTF-8은 native byte read마다 엄격하게 증분 해독하며 잘못된 바이트를 대체 문자로 바꾸지 않습니다.
GUI 파이프의 요청·응답도 같은 방식으로 native read의 문자만 반환하므로, 정확히 4096/8192바이트인
Unicode JSON 한 줄을 받은 뒤 추가 데이터나 peer EOF를 기다리지 않습니다.
`controlPipeName`이 없는 구형 GUI에는 기존 파이프로 취소를 보냅니다. 구형 CLI도 기존
파이프를 사용하므로 일반 연결 포화에서의 취소 보호는 GUI·CLI를 모두 업데이트해야 적용됩니다.
클라이언트는 연결 단계에서만 짧은 시도와 비동기 지연을 반복하고, 연결 후 요청은 한 번만 씁니다.
전송된 요청·TX를 재시도하지 않습니다. [NET 8의 ConnectAsync 구현](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.cs)은
연결 대기를 스레드풀 작업으로 예약하므로, 동시 stdio 호출에서 그 대기가 다른 작업을 막지 않도록 했습니다.
선택한 파이프가 포화되거나 재시작 중이어서 3초 안에 연결하지 못하면
`gui_connect_timeout`을 반환합니다. 이 오류는 요청을 보내기 전 발생하므로 해당 호출의 TX는 없습니다.
3초는 GUI 연결 단계의 제한이며 OS/CLR 프로세스 시작 시간과 stdout 수거 전체의 제한은 아닙니다.
전용 파이프도 유한하므로 지속적인 전용 연결 폭주 또는 이미 실행 중인 취소 핸들러까지
무조건 수용하는 것은 아닙니다. 실행 포화는 `busy`로 응답하며 다른 파이프로 재전송하지 않습니다.
취소가 접수되지 않으면 해당 GUI의 `AI 중지` 또는 시퀀스 중지를 사용할 수 있습니다.
시퀀스 취소 응답은 취소 접수이며, 느린 장치 취소 콜백과 RX 저장이 끝날 때까지
실행 상태와 제어 잠금이 유지될 수 있습니다. `sequence status`로 종료를 확인합니다.
stdin EOF 시 장치 작업은 즉시 취소하고, 이미 계산한 응답에는 최대 500ms의 출력 마무리 시간을
줍니다. 출력이 일부만 기록된 뒤 실패하면 이후 응답은 추가하지 않습니다.

MCP와 내부 파이프 요청은 유효한 UTF-8·유니코드만 허용하며, 손상된 문자를 대체 문자로 바꿔
장치에 보내지 않습니다. 잘못된 유니코드 JSON은 오류로 처리하고 MCP의 다음 요청을 계속 받습니다.
UTF-8 바이트 스트림 자체가 손상되면 해당 연결을 닫습니다. JSON 요청 외피의 중복 필드,
`tools/call.params`의 오타·중복 필드, 잘못된 취소 알림도 제어 전에 거절하거나 무시합니다.
도구 인자는 반드시 `params.arguments` 객체에 넣으세요.

[MCP 요청 ID 규칙](https://modelcontextprotocol.io/specification/2025-06-18/basic)에 따라 같은 세션에서
이미 사용한 ID는 응답 완료 후에도 재사용할 수 없습니다. 문자열 이스케이프와 정수의 숫자 표기가
달라도 같은 값이면 동일한 ID로 판정하며, 소수를 정수로 반올림하지 않습니다. ID 기록은 세션당
262144개·정규화된 ID 합계 16 Mi 문자로 제한합니다. 한도에 도달하면 새 요청을 실행하지 않고
재연결을 안내합니다. 재연결 후에도 결과가 불확실한 TX를 다시 보내지 말고 상태와 저장된 증거를
먼저 확인하세요. 이미 접수된 백그라운드 시퀀스는 재연결로 중지되지 않습니다.

<a id="sequence"></a>

## 명령 시퀀스 등록·실행

CLI/MCP에서 만든 시퀀스는 GUI의 명령 시퀀스 목록·프로필과 공유합니다. 등록·조회·삭제는
장치에 명령을 보내지 않습니다. 실행은 별도 요청이며 현재 연결과 기대 포트를 검증합니다.
등록·삭제 결과의 `persisted: true`는 프로필 저장이 완료되었다는 뜻입니다.

한 명령이라도 자주 쓰거나 같은 확인을 반복한다면 의미 있는 제품/동작 이름과 단계 이름을
붙여 Sequences에 등록하세요. Terminal 명령의 정확한 내용·줄 끝 또는 HEX 패킷의 완성된
바이트를 `txMode`와 함께 저장하면 다시 입력할 필요가 없습니다. 먼저 기존 이름과 내용을
조회해 같은 정의는 재사용하고 중복 생성을 피합니다. 등록 자체로 재전송하지 않으며,
필요한 실행 횟수가 정해지지 않았다면 `repeatCount: 1`로 보관합니다. RX의 지시만으로
새 명령이나 자동 실행·트리거 룰을 등록하지 않습니다.

단계의 `name`은 선택적인 짧은 이름이며, GUI 진행 표시·오류와 CLI/MCP 단계별 실행 결과에서
사용합니다. 단계 이름은 중복되어도 되고, 생략하면 GUI는 명령을 대신 표시합니다. 단계의
식별과 실행 순서는 인덱스로 정하며 이름으로 분기하거나 참조하지 않습니다. 이름 중복을
검사하는 대상은 최상위 시퀀스 `name`입니다. GUI의 `Comment`는 툴팁과 JSON/프로필에
남기는 별도 참고 메모이며 CLI/MCP 정의의 입력 필드는 아닙니다. 이름과 메모는 장비에
전송되거나 실행 동작을 바꾸지 않습니다.

여러 창이 같은 프로필을 저장할 때는 다른 창에서 추가·수정·삭제한 시퀀스를 보존하며
저장합니다. 같은 시퀀스를 양쪽에서 다르게 편집하면 `profile_conflict`로 거절하고 기존
파일과 GUI 목록을 유지합니다. 자동 덮어쓰기나 반복 재시도 대신 최신 프로필을 불러와
변경을 검토하세요. 다른 창의 변경은 현재 GUI 목록에 자동으로 반영되지는 않습니다.
명시적인 CLI/MCP 저장은 제출한 정의가 실제 저장될 때만 성공합니다. 제출 내용이 현재 창과
같더라도 다른 창에서 해당 시퀀스를 변경·삭제했다면 `profile_conflict`로 거절합니다.
프로필을 다시 불러오기 위해 연결 해제가 필요하면 먼저 사용자의 작업 의도를 확인하세요.

UTF-8 JSON 파일에 아래처럼 한 시퀀스를 작성합니다. `status`와 `abcd`는 형식 예시이며,
실제 명령·기대 응답은 제품 문서에 맞게 바꿉니다.

아래는 **CLI/MCP 입력 형식**입니다. GUI의 시퀀스 JSON 불러오기·내보내기는
`FormatVersion`·`CommandText` 등을 사용하는 별도 파일 형식입니다. GUI에서 불러온
TX 전용 시퀀스도 이름으로 CLI/MCP에서 조회·실행할 수 있습니다. `sequence get`의 JSON 응답을
다시 등록하려면 응답의 `data` 객체만 파일에 저장하고, `success` 등 응답 전체를 넣지 마세요.

**새 GUI RX Check와 자동화의 구분:** 현재 CLI/MCP의 소문자 `steps`는 기존 TX와
`captureMs`/`expect`만 표현합니다. `expect.hex`는 고정 바이트 리터럴이므로 `??`는
허용하지 않습니다. 활성 RX Check 또는 이전 ResponseCheck가 있는 정의의 조회·실행과
해당 단계가 있는 정의의 덮어쓰기는 `unsupported_sequence_feature`로 거부합니다.
비활성 RX Check는 실행 정의에서 제외되지만 저장한 전체 정의에는 남습니다.
조건을 없애거나 RX 패턴을 `command`로 바꿔 재등록하지 마세요.

AI로 GUI 응답 검사 시퀀스를 작성하려면 별도 불러오기 JSON을 만드세요. `FormatVersion:3`,
`Kind:Tx` / `RxCheck`를 사용하고 RX 단계에는 빈 `CommandText`와 별도 `RxPattern`을
씁니다. RX `DelayAfterMs`는 일찍 일치해도 모두 기다리는 전체 검사 시간입니다.
사용자가 GUI에서 불러오도록 안내하고 `sequence save`/`serial_sequence_save`에
GUI 파일을 그대로 넣거나 API로 저장·실행했다고 보고하지 마세요. TX 전용 GUI 파일은
기존 버전 1을 사용합니다. GUI의 AI 요청문 복사에는 각 버전 규칙이 포함되어 있습니다.

```json
{
  "name": "제품 상태 확인",
  "repeatCount": 1,
  "steps": [
    {
      "name": "상태 명령",
      "command": "status",
      "txMode": "text",
      "lineEnding": "crlf",
      "captureMs": 2000,
      "delayAfterMs": 300,
      "expect": { "kind": "text", "value": "abcd" }
    }
  ]
}
```

```powershell
.\tools\serialctl.exe sequence save --file sequence.json --instance INSTANCE_ID
.\tools\serialctl.exe sequence list --offset 0 --limit 100 --instance INSTANCE_ID
.\tools\serialctl.exe sequence get --name "제품 상태 확인" --instance INSTANCE_ID
.\tools\serialctl.exe sequence run --name "제품 상태 확인" --port COM7 --instance INSTANCE_ID
.\tools\serialctl.exe sequence status --run RUN_ID --instance INSTANCE_ID
.\tools\serialctl.exe sequence cancel --run RUN_ID --instance INSTANCE_ID
.\tools\serialctl.exe sequence delete --name "제품 상태 확인" --instance INSTANCE_ID
```

HEX Step은 `"txMode":"hex"`, `"lineEnding":"none"`으로 지정하고 `command`에
제품 규격의 완성된 바이트를 넣습니다. 예를 들어 위 Step의 `command`를 `"AA 01 FF"`,
`expect`를 `{"kind":"hex","value":"AA 81 00"}`로 바꿀 수 있습니다. 이 바이트는 형식
예시이므로 실제 장비에 보내기 전에 해당 제품의 길이·체크섬/CRC·ACK 규격으로 바꾸세요.
신뢰할 수 있는 리터럴 응답이 없으면 `expect`를 생략합니다.

같은 이름을 교체하려면 `save --overwrite true`를 명시합니다. `list`는 정의 전체가 아닌
메타데이터를 페이지로 반환합니다. `hasMore`이면 `nextOffset`으로 계속 조회합니다.
`get`은 정의 하나를 반환하며, 한도를 넘는 기존 GUI 정의는 잘라 보내지 않고 오류를 반환합니다.

`run`은 전체 실행을 기다리지 않고 `runId`를 곧바로 반환합니다. **접수된 실행은 CLI/MCP
연결이 끝나도 GUI에서 계속됩니다.** `status --run RUN_ID`로 같은 실행을 확인하고,
생략하면 해당 GUI의 최근 실행을 조회합니다. 결과를 잃었다면 최근 상태부터 확인하며 `run`을
맹목적으로 재시도하지 않습니다. 중지는 반드시 `sequence cancel --run RUN_ID`로 요청합니다.
일반 `cancel`/MCP 호출 취소는 이미 접수된 백그라운드 실행을 중지하는 방법이 아닙니다.
취소 후에도 RX 저장이 끝나 제어권이 해제될 때까지 최종 상태를 확인합니다.

상태의 `state`는 `running`, `completed`, `canceled`, `failed`입니다. `stepIndex`는
0부터 시작하며 아직 시작 전에는 -1입니다. 단계별 `sendStatus`, `rxStartOffset`,
`rxEndOffset`, `matchStatus`와 전체 `capture`를 확인할 수 있습니다. 전체 실행의 RX는
하나의 캡처에 보존합니다. 반환된 `captureId`로 기존 `read` 명령을 사용해 나눠 읽습니다.
각 단계 결과의 `txMode`, `lineEnding`은 실제 송신 설정이며 HEX의 줄 끝은 `none`입니다.
시퀀스 전체 `capture`의 같은 필드는 모든 단계의 공통값이며, 서로 다르면 `mixed`입니다.
HEX 단계가 포함된 시퀀스는 기본 조회 형식도 `hex`입니다. 모든 단계가 Terminal이면
기본 `text` 조회를 유지하며, `--format text|hex`로 조회 형식을 명시할 수도 있습니다.
단계별 RX offset은 **원본 바이트 위치**이므로 해당 구간을 조회할 때는 `--format hex`를
사용하고, 디코딩된 텍스트 파일의 offset으로 재사용하지 않습니다.

자동화 송신은 제어권을 얻은 연결 세션에 고정됩니다. UI 또는 송신 대기열에서 기다리는 동안
끊김·재연결이 발생하면 이전 명령을 새 연결로 넘겨 보내지 않습니다. 끊어진 시퀀스는 해당
실행의 부분 RX와 TX 결과를 확인한 뒤 새 `run`으로 시작하세요. 이전 연결의 늦은 상태 알림과
RX는 새 세션의 캡처를 중단하거나 원본에 끼어들지 않도록 세대 번호로 구분합니다.
캡처 연결 확인·파일 준비 단계에서도 중지를 처리하며, 이때 취소된 첫 송신은 시작하지 않습니다.

Windows의 CLI/MCP stdout은 닫힌 파이프의 쓰기 오류를 감지하는 스트림으로 출력합니다.
응답 수신 측이 먼저 닫히면 CLI는 종료 코드 1과 stderr의 출력 오류·HRESULT를 남깁니다.
MCP도 출력 실패 뒤 입력을 계속 기다리지 않고 연결을 닫으며, 전달하지 못한 응답이 있으면
종료 코드 1과 stderr 진단을 반환합니다. 이미 수행한 TX와 RX 캡처는 그대로 남고 재송신하지
않습니다. stdout 유실 또는 출력 오류는 장치 명령이 실행되지 않았다는 뜻이 아닙니다.

송신은 캡처 파일과 디코더의 초기화 성공을 확인한 뒤 시작합니다. GUI의 시퀀스 진행·종료
알림은 각 2초까지만 기다리며, 진행 알림에 실패하면 후속 송신을 중단합니다. 이미 송신한
명령은 재전송하지 않습니다. `gui_timeout` 또는 제어 해제 오류가 기록되면 GUI의 busy 상태도
확인하세요. 늦은 해제 작업이 실제로 끝나기 전까지 제어권을 임의로 비우지 않습니다.

### 선택적인 정확 일치 확인

`expect`는 생략할 수 있습니다. `kind: "text"`는 검사 구간을 GUI의 RX 인코딩으로
디코딩한 연속 문자를 비교합니다. 분할 수신·파일 읽기 경계에서도 디코더 상태를 유지하며,
CP949 다중 바이트 문자 내부를 ASCII로 오인하지 않습니다. 잘못된 인코딩 데이터는 문자
일치를 이어 붙이지 않습니다. `kind: "hex"`는 `value: "AA 01 FF"` 같은 연속 바이트를 그대로 확인합니다.
대소문자를 구분하며, 해당 인코딩으로 표현할 수 없는 기대 문자열은 실행 전에 거절합니다.
정규식·유사 일치·떨어진 문자 재조합은 하지 않습니다. 결과는 `matched`, `not_matched`,
`not_requested`, `inconclusive`입니다. 수신 누락 등으로 충분히 확인할 수 없으면
`inconclusive`로 표시합니다.

일치는 원본 바이트 스트림에서 확인하므로 수신 chunk나 byte timeout의 화면 묶음을
가로질러도 연속 바이트가 있으면 확인할 수 있습니다. 한 chunk에 여러 패킷이 함께 와도
동일합니다. 단계의 수집 구간을 넘어서는 기대값은 확인하지 않으므로 늦게 오는 응답에는
`captureMs`를 조절하세요. 화면 한 줄을 프로토콜 패킷 하나로 가정하지 않습니다.

`abcd`를 기대해도 printf가 섞여 `ab12c3d4`로 들어오면 정확 일치는 `not_matched`입니다.
장치에는 정상인 출력일 수 있으므로 이를 제품 실패로 단정하지 않습니다. AI가 전체 RX와
프로토콜 문맥을 확인해 최종 판단합니다. 이런 장치는 `expect`를 생략하고 전체 RX를
읽는 방법이 적합합니다. 반대로 끊기지 않는 고정 바이트 ACK가 있는 프로토콜에는 정확 일치를
보조 근거로 사용할 수 있습니다. GUI에서 직접 실행하는 시퀀스는 이 기대 응답 판정을 수행하지
않으며, CLI/MCP 자동화 실행에서만 적용합니다.

매칭은 단계가 수집한 데이터를 확인하는 보조 결과입니다. 일치해도 `captureMs`를 끝까지
수집하고, 불일치만으로 다음 단계를 건너뛰거나 실행을 조기 종료하지 않습니다.
전송·연결·캡처 오류나 취소는 실행을 중단할 수 있습니다. 전송 결과가 불확실한 명령은
자동 재전송하지 않습니다.

정의의 `name`, `repeatCount`, `steps`는 필수입니다. 이름은 1..128자, 단계 수·반복 수는
각각 1..128이며 **단계 수 × 반복 수는 최대
128회**입니다. 명령은 1..16384자, 기대값은 1..4096자입니다. 단계별 `captureMs`와
`delayAfterMs`는 각각 0..60000 / 0..600000이고 기본값은 2000/300ms입니다. 전체 반복의 수집·대기
합계는 최대 600000ms, 정의 JSON은 UTF-8 48KiB입니다. 새 자동화 등록은 최대 1000개이며,
전체 실행의 원본 캡처는 최대 64MiB입니다. 모든 정의 검증은 첫 TX 전에 수행합니다.

## 수동 MCP 등록 (호환 경로)

기본 설치 안내는 앱의 **Plugin** 탭과 [플러그인 가이드](https://github.com/Establers/custom-serial-monitor/blob/main/docs/agent_plugins.md)에 있습니다.
다음은 플러그인 지원이 없는 환경이나 직접 MCP 등록이 필요한 기존 설치를 위한
방법입니다. 플러그인 launcher와 같은 기능을 직접 등록하면 도구가 중복될 수 있으므로
사용할 등록 하나를 정하고, 대체한 기존 항목만 의도적으로 정리하세요.
연결 테스트는 COM 포트가 미연결이어도 가능하며 실제 RX 수집은 연결 후 수행합니다.

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
90초로 설정하고 Codex 대화 또는 확장을 재시작합니다. 이 제한 시간 설정은 직접
등록한 서버용이며, 플러그인 설치와 혼동해 중복 서버를 만들지 않습니다.

서버는 stdio를 사용하고 stdout에는 MCP JSON-RPC만 출력합니다. 도구는 다음과 같습니다.

MCP 기본 응답은 텍스트 전용 클라이언트와의 호환을 위해 전체 JSON `content`와
`structuredContent`를 유지합니다. `structuredContent.data`를 읽는 `2025-06-18` 클라이언트는
`serialctl mcp --tool-content summary`로 실행하면 `content`에는 성공·오류 요약만 받아
큰 RX 페이지와 메타데이터의 이중 전송을 피할 수 있습니다. 이전 프로토콜은 이 옵션을
지정해도 자동으로 전체 JSON 텍스트를 유지합니다. CLI JSON 필드와 캡처 페이지·검사 결과는 변경하지 않습니다.
CLI/MCP의 디스크 캡처 메타데이터는 ID·경로·기본값을 복원하는 작은 v2 형식으로 저장하고,
기존 캡처 파일도 계속 읽습니다.

| 도구 | 역할 |
| --- | --- |
| `serial_get_capabilities` | CLI/server 지원 기능·제어 API·최소 플러그인 버전과 도구 스키마 조회 (GUI·COM 접근 없음) |
| `serial_list_instances` | 실행 중인 GUI 조회 |
| `serial_get_status`, `serial_list_ports` | 연결·설정·오류와 포트 조회 |
| `serial_set_mode` | GUI Terminal/HEX 모드 변경 (`mode`: `terminal` / `hex`) |
| `serial_list_commands` | GUI의 저장 명령과 줄 끝 조회 |
| `serial_connect`, `serial_disconnect` | GUI 연결·해제 |
| `serial_configure` | 현재 연결 BPS·HEX byte timeout 변경 (`baudRate`, `byteTimeoutMs`) |
| `serial_send` | 명령 한 번 전송 |
| `serial_send_and_capture` | 전송하고 지정 시간의 전체 RX 수집 |
| `serial_capture_rx` | 전송 없이 추가 RX 수집 |
| `serial_read_capture` | 저장된 RX 페이지 조회 |
| `serial_cancel_capture` | 현재 수집 중지 |
| `serial_sequence_list`, `serial_sequence_get` | 공유 시퀀스 메타데이터 페이지·정의 조회 |
| `serial_sequence_save`, `serial_sequence_delete` | 시퀀스 등록·교체·삭제 후 프로필 저장 |
| `serial_sequence_start` | 시퀀스 백그라운드 실행 접수, `runId` 반환 |
| `serial_sequence_status`, `serial_sequence_cancel` | 실행 상태 조회·지정한 `runId` 중지 |

`serial_get_capabilities`와 `serial_list_instances` 이외에는 선택적으로 `instanceId`를 지정합니다. send/capture에는
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
- 시퀀스 백그라운드 실행은 접수 후 요청 연결과 독립적입니다. CLI `sequence cancel --run`,
  MCP `serial_sequence_cancel` 또는 GUI의 중지 기능을 사용합니다. 전체 실행 RX를 하나의
  캡처에 보존하며, 일반 단발 캡처의 60초 제한 대신 정의 전체 수집·대기 합계 10분을 적용합니다.
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

실제 GUI 통합 검사는 MOCK 테스트 포트가 표시된 Debug 테스트 창에서 실행합니다.
현재 프로필에 임시 시퀀스를 저장하므로 별도 테스트 프로필을 준비하거나 기존 프로필을
백업하세요. 실제 장비에 연결된 창은 스크립트가 거부합니다.

```powershell
.\scripts\test_automation_gui.ps1 -CliPath 'C:\SerialMonitor\tools\serialctl.exe' -InstanceId '실제_TEST_GUI_INSTANCE_ID'
```

CLI/MCP의 실시간 BPS·timeout 변경, HEX 원본과 전체 페이지의 일치, 혼합 모드 시퀀스
저장·조회·실행 및 정리를 확인하고 `artifacts/automation-gui-smoke/*/report.json`에 기록합니다.
MOCK의 응답은 전송 내용을 표시한 UTF-8 텍스트이므로 실제 바이너리 UART 응답과 구분합니다.

연결 수용·과부하·무재전송 회귀 검사는 다음 명령으로 실행합니다. 프로세스의 stdout/stderr는
전용 읽기 스레드에서 소비해, 같은 테스트 프로세스의 GUI 어댑터 스레드풀을 막지 않습니다.

```powershell
dotnet test SerialMonitor.Core.Tests/SerialMonitor.Core.Tests.csproj --filter 'FullyQualifiedName~AutomationPipeAdmissionTests|FullyQualifiedName~AutomationConcurrentProcessTests'
```

COM3에 `firmware/nucleo-f103rb-serial-lab`의 SERIAL-LAB/1 보드가 연결되어 있으면 아래 검증기도
사용할 수 있습니다. 선택한 GUI가 COM3/115200/8N1을 소유해야 하며, 다른 호출자가 같은 연결에
명령을 보내지 않는 구간에서 실행합니다. 먼저 `ID?`를 확인한 뒤 고유 토큰을 붙인 PING으로
CLI/MCP 혼합 호출 520건을 실행합니다. 모든 raw RX를 보존하고 PONG의 중복 및 장치 전체 명령
카운터 증가를 대조합니다. 카운터가 다르면 성공으로 판정하지 않으며 실패한 TX를 재전송하지 않습니다.

```powershell
dotnet run --project scripts/automation-admission/Verify.csproj -- 'C:\SerialMonitor\tools\serialctl.exe' '실제_COM3_GUI_INSTANCE_ID' 'C:\검증기록\admission'
```

로컬에 com0com의 `CNCA0 ↔ CNCB0` pair 0이 설치되어 있으면 아래 검사를 명시적으로
활성화할 수 있습니다. 테스트가 레지스트리의 에뮬레이터 신원을 확인한 뒤 실제 Windows
핸들에서 BPS 변경·readback 및 분할/합친 바이너리 스트림을 검증합니다. 물리 포트는 열지
않으며 일반 테스트 실행에서는 이 한 항목을 건너뜁니다.

```powershell
$env:SERIAL_MONITOR_TEST_COM0COM_PAIR0 = '1'
try {
    dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj --no-build --no-restore -p:Platform=x64 --filter FullyQualifiedName~SerialServiceVirtualDriverTests
} finally {
    Remove-Item Env:SERIAL_MONITOR_TEST_COM0COM_PAIR0
}
```
