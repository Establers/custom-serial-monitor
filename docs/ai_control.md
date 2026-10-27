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

`--text-file`은 UTF-8 명령 파일을 데이터로 읽어 따옴표·줄바꿈을 그대로 보존합니다.
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
프로필을 다시 불러오기 위해 연결 해제가 필요하면 먼저 사용자의 작업 의도를 확인하세요.

UTF-8 JSON 파일에 아래처럼 한 시퀀스를 작성합니다. `status`와 `abcd`는 형식 예시이며,
실제 명령·기대 응답은 제품 문서에 맞게 바꿉니다.

아래는 **CLI/MCP 입력 형식**입니다. GUI의 시퀀스 JSON 불러오기·내보내기는
`FormatVersion`·`CommandText` 등을 사용하는 별도 파일 형식입니다. GUI에서 불러온
시퀀스도 이름으로 CLI/MCP에서 조회·실행할 수 있습니다. `sequence get`의 JSON 응답을
다시 등록하려면 응답의 `data` 객체만 파일에 저장하고, `success` 등 응답 전체를 넣지 마세요.

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
단계별 RX offset은 **원본 바이트 위치**이므로 해당 구간을 조회할 때는 `--format hex`를
사용하고, 디코딩된 텍스트 파일의 offset으로 재사용하지 않습니다.

### 선택적인 정확 일치 확인

`expect`는 생략할 수 있습니다. `kind: "text"`는 기대 문자열을 GUI의 RX 인코딩으로
변환한 연속 바이트를, `kind: "hex"`는 `value: "AA 01 FF"` 같은 연속 바이트를 확인합니다.
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
