<div align="center">
  <img src="icon.PNG" width="96" alt="Serial Monitor icon" />
  <h1>Serial Monitor</h1>
  <p><strong>오래 연결해 두어도 버벅이지 않고, 놓친 오류를 다시 찾을 수 있는 Windows 시리얼 모니터</strong></p>
  <p>
    <a href="https://github.com/Establers/custom-serial-monitor/releases/latest"><img src="https://img.shields.io/github/v/release/Establers/custom-serial-monitor?display_name=tag&style=flat-square" alt="Latest release" /></a>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows" alt="Windows 10 and 11" />
    <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 8" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-2ea44f?style=flat-square" alt="MIT License" /></a>
  </p>
  <p>
    <a href="https://github.com/Establers/custom-serial-monitor/releases/latest/download/SerialMonitorSetup.exe"><strong>프로그램 다운로드</strong></a>
    ·
    <a href="#ai-plugin-install"><strong>AI Plugin 설치</strong></a>
    ·
    <a href="docs/agent_plugins.md#share">다른 사람에게 공유</a>
    ·
    <a href="docs/release_notes_v1.6.2.md">v1.6.2 변경 사항</a>
    ·
    <a href="docs/manual_test_checklist.md">테스트 체크리스트</a>
  </p>
</div>

## 다운로드

| 사용 방법 | 다운로드 경로 | 시작 방법 |
| --- | --- | --- |
| 일반 설치 | [프로그램 설치 EXE](https://github.com/Establers/custom-serial-monitor/releases/latest/download/SerialMonitorSetup.exe) | 프로그램 설치 위치 선택 → 설치 후 실행 |
| 프로그램 포터블 | [프로그램 포터블 ZIP](https://github.com/Establers/custom-serial-monitor/releases/latest/download/SerialMonitorPortable.zip) | ZIP 전체를 풀고 `SerialMonitor.WinUI.exe` 실행 |
| AI 플러그인 | [GitHub 저장소](https://github.com/Establers/custom-serial-monitor) | 아래에서 사용할 AI 도구의 설치 명령 실행 |

[릴리스 설명과 SHA-256 체크섬](https://github.com/Establers/custom-serial-monitor/releases/latest)도 제공합니다.

<a id="ai-skill-install"></a>
<a id="ai-plugin-install"></a>

## AI Plugin 빠른 설치

프로그램 v1.6.0 이상을 설치하고 GUI를 실행한 뒤, **사용할 AI 도구의 명령만** 실행하세요.
플러그인은 [GitHub 저장소](https://github.com/Establers/custom-serial-monitor)에서 설치·업데이트합니다.

```powershell
# Codex 데스크톱·CLI
codex plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
codex plugin add serial-monitor@serial-monitor-tools

# Claude Code
claude plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
claude plugin install serial-monitor@serial-monitor-tools --scope user

# GitHub Copilot CLI
copilot plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
copilot plugin install serial-monitor@serial-monitor-tools
```

설치 후 AI 도구에서 플러그인 활성 상태를 확인하고 새 세션을 시작합니다. 먼저 읽기 전용으로
연결을 확인하세요.

> Serial Monitor 플러그인의 지원 기능과 실행 중인 GUI 목록을 조회해줘.
> 대상 GUI의 연결 상태와 저장 명령을 알려주고, 명령 전송이나 연결 변경은 하지 마.

프로그램은 인스톨러로, 플러그인은 AI 도구의 갱신 기능으로 업데이트합니다.
VS Code 설치·업데이트와 기존 skill에서 전환하는 방법은
[플러그인 가이드](docs/agent_plugins.md)를 참고하세요.

![Cute background를 끈 상태에서 MOCK 로그와 탐지 이벤트를 표시하는 Serial Monitor](docs/images/serial-monitor-overview.png)

Serial Monitor는 UART 로그를 몇 분이 아니라 몇 시간, 며칠 동안 관찰하는
임베디드·RTOS 디버깅을 위해 만든 WinUI 3 데스크톱 앱입니다. 화면에는 최근
로그만 제한적으로 유지하고 전체 로그는 별도 작업에서 디스크에 기록합니다.
수신, 파싱, 파일 기록, 이벤트 탐지, UI 표시를 분리해 빠른 데이터가 계속 들어와도
UI 응답성과 메모리 사용량을 예측 가능하게 유지하는 것이 핵심입니다.

## 핵심 사용 흐름

1. COM 포트와 baud rate를 선택해 연결합니다.
2. Terminal 또는 HEX 모드로 RX/TX 데이터를 확인합니다.
3. 필요한 세션은 `LOG ON`으로 전체 기록을 남깁니다.
4. `WARN`, `ERROR`, 재부팅 문자열 같은 규칙을 등록해 이벤트와 전후 문맥을
   자동으로 모읍니다.
5. 검색 결과, 사용자 마커, 하단 상태 카운터로 문제가 발생한 시점을 좁힙니다.
6. 저장 명령과 시퀀스로 같은 재현 절차를 반복합니다.
7. 필요한 경우 `로그 내보내기...`로 현재 보관 중인 로그를 별도 파일에 저장합니다.

## 왜 장시간 디버깅에 적합한가요?

- **화면 메모리가 계속 자라지 않습니다.** UI 로그와 이벤트는 bounded buffer로
  제한하고, 전체 기록은 일반 텍스트 파일에 비동기로 저장합니다.
  로그 화면은 행 수 외에도 보관 데이터 64MiB, 표시 문자열 16Mi 문자,
  화면 대기 큐 32MiB로 제한합니다. 개행 없는 연속 수신은 기록 경계에서
  표시 줄을 나눕니다. 일반 수신 기록은 전체 내용을 표시하며, 오래된 화면
  기록을 지워도 룰·이벤트·RX Check 판정과 원본 파일 로그에는 영향을 주지 않습니다.
- **수신 경로에서 UI나 디스크를 기다리지 않습니다.** background channel과
  queue가 컴포넌트를 분리하며, 과부하는 drop/error 카운터로 드러납니다.
- **오류 한 줄만 남기지 않습니다.** 키워드 규칙이 일치한 시점의 앞뒤 문맥까지
  함께 캡처해 재현 전후 상태를 확인할 수 있습니다.
- **백그라운드 실패를 숨기지 않습니다.** 파일, 파이프라인, bridge 오류와 큐
  상태를 하단 Health 표시와 원인 툴팁에서 확인하고 우클릭으로 복사할 수 있습니다.
- **반복 작업을 프로필로 재사용합니다.** 포트 설정, 룰, 하이라이트, 저장 명령,
  명령 시퀀스를 JSON 프로필로 저장하고 다시 불러올 수 있습니다.

## 5분 안에 시작하기

### 1. 설치

[Releases](https://github.com/Establers/custom-serial-monitor/releases/latest)에서
다음 중 하나를 받습니다.

- `SerialMonitorSetup.exe`: 일반 설치 버전
- `SerialMonitorPortable.zip`: 설치와 관리자 권한이 필요 없는 포터블 버전

포터블 버전은 ZIP 전체를 푼 뒤 `SerialMonitor.WinUI.exe`를 실행하세요. EXE만
따로 옮기거나 ZIP 안에서 바로 실행하면 안 됩니다.

> Windows 10/11 x64용입니다. xterm 로그 화면에는 Microsoft Edge WebView2
> Runtime이 필요합니다. 설치 파일은 현재 코드 서명이 없어 SmartScreen에서
> 알 수 없는 게시자 경고가 표시될 수 있습니다.

### 2. 장치 연결

먼저 `Port` 왼쪽의 `Terminal`/`HEX` 모드를 확인한 뒤 `Port`와 `Baud`를 선택하고
`Connect`를 누릅니다. 모드 드롭다운의 각 항목에 간단한 설명이 표시됩니다. 데이터 형식과 줄 끝,
encoding, Terminal/HEX 모드는 `Settings`에서 조정할 수 있습니다.

### 3. 장치 없이 둘러보기

Debug 빌드에서는 `Test` 탭의 `Show MOCK test port`를 켠 뒤 `[TEST] MOCK`에
연결할 수 있습니다. 일반 로그, WARN/ERROR 주입, burst와 stress 동작을 실제
파이프라인으로 확인할 때 사용합니다. 배포용 Release 빌드에서는 MOCK 포트가
표시되지 않습니다.

### 4. 전체 로그 저장

`Log` 탭에서 저장 폴더, 파일 이름, 회전 크기를 정한 뒤 `LOG ON`을 누릅니다.
HEX 모드의 수신 데이터는 byte-exact 16진수 텍스트로 기록되며 raw `.bin` 기록은
제공하지 않습니다.

실시간 파일 기록을 켜기 전의 로그도 메모리에 남아 있다면 상단의 `로그 내보내기...`로
저장할 수 있습니다. 버튼을 누른 시점의 보관 기록을 필터로 숨겨진 기록까지 포함해
별도 파일로 내보내며, 저장 중에도 수신과 실시간 파일 기록은 계속됩니다.
보관 한도나 Clear로 제거된 기록은 포함되지 않습니다.

상단 `Filter`는 보관 중인 로그 전체에 적용됩니다. 규칙을 선택하고 `Apply`를 누르면
기존 로그와 이후 들어오는 로그 중 일치하는 항목만 표시합니다. 여러 규칙은 OR 조건이며,
`ALL`을 적용하면 숨겼던 로그도 원래 순서대로 다시 표시됩니다. 필터는 로그를 삭제하지
않으며 파일 기록에도 영향을 주지 않습니다. 보관 한도나 Clear로 제거된 로그는 복원되지 않습니다.

## 주요 기능

`About`에서 설치 버전과 업데이트 상태를 함께 확인할 수 있습니다.
`Check for updates`를 눌렀을 때만 새 버전을 확인합니다. 새 정식 버전이 있으면 하단 상태줄
오른쪽에 작은 `Update v…` 버튼이 표시됩니다. 클릭하면 About으로 이동하며, 로그 위에
팝업을 띄우거나 작업 화면을 자동 전환하지 않습니다. `View release`로 변경 사항과
다운로드 페이지를 열 수 있고, 옆의 `…` 메뉴에서 해당 버전 알림을 건너뛸 수 있습니다.
수동 확인하면 알림을 건너뛴 버전도 이번 실행에서 다시 표시됩니다.
자동 설치나 재시작은 하지 않습니다. 마지막 확인 결과는 재실행 후에도 유지되며,
설정은 프로필과 별도로 `%LOCALAPPDATA%\SerialMonitor\updates.json`에 저장됩니다.
GitHub API 요청 한도를 초과하면 GitHub Pages의
[`updates.json`](https://establers.github.io/custom-serial-monitor/updates.json)으로 대체 확인합니다.
API와 대체 요청의 네트워크 대기는 합계 최대 10초이며, 일반 접속 오류에는 대체 요청을 하지 않습니다.
안내 파일은 정식 릴리스 게시 후 자동 갱신됩니다. 자세한 배포 절차는
[업데이트 확인 문서](docs/update_ui_testing.md#static-fallback-manifest)를 참고하세요.
개발 중에는 [업데이트 UI 미리보기](docs/update_ui_testing.md)로 새 버전·최신·오프라인·확인 중 상태를 검증할 수 있습니다.

| 영역 | 제공 기능 |
| --- | --- |
| RX 표시 | 실제 COM 포트, Terminal/HEX 모드, UTF 계열·코드 페이지 디코딩, timestamp와 RX/TX prefix |
| 연결 복구 | 실제 COM의 예기치 않은 읽기/쓰기 실패 후 같은 포트 자동 재연결, 1·2·5·10초 backoff, 수동 취소 |
| 대용량 화면 | xterm.js 기반 선택·복사, 선택 구간 시간차 및 HEX byte 길이 툴팁, batched append, bounded UI log buffer, pause/auto-scroll |
| 파일 기록 | 비동기 plain-text 기록, 크기 기반 회전, 사용자 파일 이름, flush/error 카운터 |
| 이벤트 | Terminal/HEX 전용 규칙, 전후 문맥, 일치 문자열 강조, 트레이 알림 |
| 검색 | 현재 보관 중인 로그 검색, F3/Shift+F3/F4, 결과 목록, 1,000줄 단위 페이지 |
| TX | 수동 전송, 줄 끝 설정, 저장 명령, quick button, 명령 history, 사용자 marker |
| 자동화 | 명령 시퀀스·1~9,999회 반복, 단계 사용 체크·복제, 시작 버튼에서 실행·일시정지·계속, 문자/HEX 응답 패턴·`??`, 별도 실행 결과 창, JSON 공유, RX 룰 기반 안전한 1회 트리거 |
| 프로필 | 설정·룰·명령·시퀀스 JSON 저장/불러오기/초기화, 이전 형식 정규화 |
| Bridge | 물리 COM과 가상 COM 사이의 선택적 양방향 raw-byte 전달 및 상태 확인 |
| 상태/장애 확인 | 하단 큐/drop/error 카운터, Health 원인 툴팁, 우클릭 상태 복사, 런타임 오류 파일 |

## 검색과 이벤트 검토

![Cute background를 끈 상태에서 WARN 검색 결과와 일치 문자열을 강조하는 Serial Monitor](docs/images/serial-monitor-search.png)

상단 검색은 현재 화면 메모리에 보관된 로그 snapshot을 대상으로 합니다. 결과는
시간, 방향, 메시지로 나뉘며 행을 더블 클릭하면 원본 로그 위치로 이동합니다.
같은 줄의 반복 일치는 한 행으로 묶고, 일치 로그가 많으면 1,000줄 단위로
페이지를 이동합니다. VS Code와 같은 `Aa`(대/소문자 구분), `|ab|`(단어 단위),
`.*`(정규식) 옵션을 조합할 수 있으며 검색창에서 `Alt+C`, `Alt+W`, `Alt+R`로
각 옵션을 전환할 수 있습니다.

로그가 계속 들어오는 동안 선택이 흔들리지 않도록 결과 목록은 수동 새로고침이
기본입니다. 새 로그를 포함하려면 `Enter`, `Refresh`, 또는 최신 페이지의 `Next`를
사용하세요. 검색어 또는 검색 옵션을 편집하는 동안에는 이전 결과를 `Stale` 상태로
유지하며, `Enter`나 `Refresh`를 실행할 때 새 결과로 교체합니다. 디스크에 기록된
전체 로그 검색은 현재 외부 텍스트 도구가 필요합니다.

`F3`는 다음 일치로, `Shift+F3` 또는 `F4`는 이전 일치로 이동합니다.

## 시간·위치 이동

상단의 `위치 이동` 또는 `Ctrl+G`로 앱의 다른 편집 창과 같은 WinUI 이동 창을 엽니다.
현재 보고 있는 시각을 기준으로 시·분·초를 선택하고 `이동`을 누릅니다.
각 항목은 마우스 휠이나 방향키로 바꿀 수 있으며, 여러 날짜가 포함되면
연·월·일도 선택할 수 있습니다. 하루의 로그만 있으면 날짜는 고정 표시합니다.
선택 범위는 보관된 첫 로그부터 창을 연 현재 시각까지이며, 창을 열어 둔 동안
수신이 계속되어도 범위와 선택값이 움직이지 않습니다. 해당 시각의 로그가 없으면
그 이후 첫 로그로, 마지막 로그 이후의 시각이면 마지막 로그로 이동합니다.

화면에서 타임스탬프 표시를 꺼도 내부 시각으로 이동합니다.
같은 이동 창의 `이전 위치`는 이동 전 위치를 최대 64개까지 자동 기억합니다.
안내와 오류는 정해진 공간에 표시되어 선택을 바꿔도 창 크기가 흔들리지 않습니다.

이동하면 자동 스크롤이 꺼지고 수신과 파일 기록은 계속됩니다.
시간 이동은 현재 화면에 보관 중인 로그를 기준으로 합니다.
실제 시각 정보가 없으면 시간 선택은 비활성화되지만, `이전 위치`는 사용할 수 있습니다.
보관 한도를 넘어 사라진 위치는 복원할 수 없음을 표시합니다.

## AI Plugin / CLI / MCP 제어

실행 중인 GUI의 연결을 Codex·Claude Code·GitHub Copilot CLI에서 사용할 수 있습니다.
`tools\serialctl.exe`로 포트 연결, Terminal/HEX 모드 조회·변경, 명령 전송, 시퀀스 등록·실행·중지, 지정 시간의 **전체 RX 수집**과 페이지 조회를
수행합니다. 제품에서 섞여 나온 printf 출력도 원문으로 AI에 전달합니다. GUI 하단에서
작업 상태를 확인하고 `AI 중지`로 수집을 끝낼 수 있습니다.
설정과 사용 예는 [AI 제어 가이드](docs/ai_control.md)를 참고하세요.

시퀀스는 `serialctl sequence save --file check.json`으로 GUI 목록과 프로필에 저장하고,
`serialctl sequence run --name "장치 확인"`으로 실행합니다. 실행 ID를 받은 뒤
`sequence status --run ID`와 `sequence cancel --run ID`로 확인·중지할 수 있습니다.
실행 시작이 승인되면 CLI를 닫아도 계속 실행되며, GUI의 `AI 중지`와 시퀀스 Stop도 사용할 수 있습니다.
등록 자체는 전송하지 않고, 기존 이름을 바꾸려면 `--overwrite true`를 명시해야 합니다.

선택적인 예상 응답은 연속된 문자열 또는 HEX 바이트의 정확한 포함 여부만 확인합니다.
`abcd`가 `ab12c3d4`로 섞여 들어오면 `not_matched`이며 제품 실패로 단정하지 않습니다.
매칭과 무관하게 정해진 시간의 전체 RX를 저장하므로, 반환된 `captureId`로 원문을 읽어 판단할 수 있습니다.
시퀀스는 단계별로 한 번만 전송하며 미매칭·전송 오류 시 자동 재전송하지 않습니다.

Codex·Claude Code·GitHub Copilot에서 같은 절차를 재사용하도록
[Serial Monitor Plugin](docs/agent_plugins.md)에 [공통 skill](skills/serial-monitor-control/SKILL.md)과
로컬 MCP launcher를 함께 제공합니다. 연결된 MCP 도구를 우선 사용하며 CLI도 지원합니다.
`serial_get_capabilities` 또는 `serialctl describe --json`으로 현재 지원 기능·API 버전을
조회하고, 새 제어 도구의 인자는 실행 중인 서버의 스키마에서 확인합니다.
플러그인 지원이 없는 환경을 위한 [기존 단독 skill 설치](docs/agent_skills.md)도 유지합니다.

앱의 **Plugin** 탭에서 클라이언트별 설치·업데이트 명령, 읽기 전용 연결 테스트,
실제 제어 예시와 문제 해결을 확인할 수 있습니다. 클라이언트가 Windows 로컬 MCP 또는
로컬 shell에 접근할 수 있어야 장치를 제어할 수 있습니다.

## 안정성을 위한 구조

```text
Serial RX
  -> bounded byte channel
  -> framing / decoding / timestamp
  -> bounded LogLine channel
       -> batched, bounded UI renderer
       -> asynchronous file writer
       -> event detector + context capture
```

serial receive callback은 UI control을 갱신하거나 파일을 쓰지 않습니다. 각
background worker는 cancellation을 지원하고, 비정상 종료를 잡아 UI에 노출합니다.
종료 시에는 producer를 먼저 멈추고 writer가 제한된 시간 안에 drain·flush하도록
정리합니다. 자세한 설계는 [아키텍처 문서](docs/architecture.md)를 참고하세요.

## 알아둘 제한 사항

- 화면 검색은 디스크 전체가 아니라 현재 retained UI buffer만 대상으로 합니다.
- 이벤트 탐지는 keyword/rule 기반이며 Terminal 룰과 HEX 룰은 서로 다른 모드에서
  동작합니다.
- command sequence는 고정 명령과 지연을 순서대로 실행합니다. 응답 분기와
  parameter template은 아직 제공하지 않습니다.
- 자동 재연결은 같은 COM 포트 이름을 다시 엽니다. 장치 고유 ID를 따라 COM
  번호 변경을 추적하지 않으며, 중단된 bridge나 command sequence를 자동으로
  다시 시작하지 않습니다.
- COM bridge는 byte를 그대로 전달하지만 modem-control line과 BREAK를 전달하지
  않으며, com0com 같은 가상 포트 드라이버는 별도로 준비해야 합니다.
- bridge의 장치 → 가상 포트 방향은 실제 COM에서 **수신한 RX**를 전달합니다.
  앱에서 실제 COM으로 보낸 TX 자체는 복사하지 않으며, 장치가 보낸 응답/echo는
  RX로 들어오므로 전달됩니다. 같은 COM 포트를 여러 프로그램이 동시에 열 수는
  없으며, 외부 프로그램은 앱이 연 가상 포트의 반대쪽 포트를 열어야 합니다.
- MOCK/stress 검증은 실제 USB-UART adapter와 장치의 장시간 검증을 대체하지
  않습니다.

전체 목록은 [Known Limitations](docs/known_limitations.md)에서 관리합니다.

## 개발 및 빌드

요구 사항은 Windows 10/11 x64, .NET 8 SDK, Visual Studio 2022 또는 동등한
MSBuild 환경입니다.

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Debug
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Release
dotnet test SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Debug
```

포터블 ZIP과 선택적 Inno Setup 설치 파일은 저장소 루트에서 만듭니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish_portable.ps1
powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1
```

출력은 `release` 폴더에 생성되며 MSIX/AppX 패키지는 사용하지 않습니다.
자세한 절차는 [포터블 배포](docs/portable_deployment.md)와
[설치 파일 배포](docs/installer_deployment.md)를 참고하세요.

## 데이터 위치

| 데이터 | 기본 위치 |
| --- | --- |
| 시리얼 로그 | `%LOCALAPPDATA%\SerialMonitor\logs` |
| 기본 프로필 | `%LOCALAPPDATA%\SerialMonitor\profiles\default.json` |
| 마지막 런타임 오류 | `%LOCALAPPDATA%\SerialMonitor\diagnostics\instance-<pid>-<launch-id>\last_runtime_error.txt` |

실행마다 별도의 `instance-*` 진단 폴더를 만듭니다. 해당 폴더의
`last_startup.txt`에 기록된 시작 시각으로 확인하려는 실행을 구분하세요.
`last_runtime_error.txt`는 오류가 발생했을 때만 생성됩니다.
`diagnostics` 바로 아래에 남은 `.txt` 파일은 이전 버전 기록이며 현재 실행에서 갱신하지 않습니다.

## 문서

- [AI Plugin 설치·업데이트·공유](docs/agent_plugins.md)
- [기존 단독 skill 설치](docs/agent_skills.md)
- [AI용 시퀀스 JSON 작성 가이드](docs/sequence_authoring.md): 복사할 AI 요청문,
  실제 필드 규격, 시퀀스 JSON 불러오기·내보내기 방법. [JSON 예제](docs/sequence_examples.json)와
  함께 빌드·배포 결과의 실행 파일 옆 `docs` 폴더에 포함됩니다.
- [요구 사항](docs/requirements.md)
- [아키텍처](docs/architecture.md)
- [로깅 동작](docs/logging.md)
- [수동 회귀 테스트](docs/manual_test_checklist.md)
- [장치 테스트 개발 가이드](docs/device-test-development-guide.md): STM32 테스트 펌웨어,
  빌드 식별, 고정 데이터와 독립 판정, CLI/MCP 실기 증거, 다른 장치에 적용하는 절차.
  [NUCLEO-F103RB 예제](firmware/nucleo-f103rb-serial-lab/README.md)와 함께 참고하세요.
- [com0com 스트레스 테스트](docs/com0com_stress_testing.md)
- [HEX RX 버스 사용량 계산](docs/bus_utilization.md)
- [v1.6.2 변경 사항](docs/release_notes_v1.6.2.md)
- [v1.6.1 변경 사항](docs/release_notes_v1.6.1.md)
- [v1.6.0 변경 사항](docs/release_notes_v1.6.0.md)
- [v1.5.2 릴리즈 노트](docs/release_notes_v1.5.2.md)
- [v1.5.1 릴리즈 노트](docs/release_notes_v1.5.1.md)
- [v1.5.0 릴리즈 노트](docs/release_notes_v1.5.0.md)
- [v1.4.2 릴리즈 노트](docs/release_notes_v1.4.2.md)
- [v1.4.1 릴리즈 노트](docs/release_notes_v1.4.1.md)
- [v1.4.0 릴리즈 노트](docs/release_notes_v1.4.0.md)
- [v1.3.8 릴리즈 노트](docs/release_notes_v1.3.8.md)
- [v1.3.7 릴리즈 노트](docs/release_notes_v1.3.7.md)
- [v1.3.6 릴리즈 노트](docs/release_notes_v1.3.6.md)
- [v1.3.5 릴리즈 노트](docs/release_notes_v1.3.5.md)
- [v1.3.3 릴리즈 노트](docs/release_notes_v1.3.3.md)
- [v1.3.2 릴리즈 노트](docs/release_notes_v1.3.2.md)
- [v1.3.1 릴리즈 노트](docs/release_notes_v1.3.1.md)
- [v1.3.0 릴리즈 노트](docs/release_notes_v1.3.0.md)
- [v1.2.5 릴리즈 노트](docs/release_notes_v1.2.5.md)

## 라이선스

[MIT License](LICENSE)로 배포됩니다. 포함된 JetBrains Mono 글꼴은
`SerialMonitor.WinUI/Assets/xterm/fonts/OFL.txt`의 SIL Open Font License를
따릅니다.
