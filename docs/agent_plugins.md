# Serial Monitor Plugin 설치와 업데이트

플러그인은 [GitHub 저장소](https://github.com/Establers/custom-serial-monitor)에서 설치합니다.
프로그램 v1.6.0 이상을 설치하고 GUI를 실행한 뒤, 같은 Windows PC의 AI 도구에서
아래 명령을 실행하세요. 플러그인 이름은 `serial-monitor`, 마켓플레이스 이름은
`serial-monitor-tools`입니다.

## 빠른 설치

사용할 도구의 명령만 실행합니다. GUI의 **Plugin** 탭에서도 복사할 수 있습니다.

### Codex 데스크톱·CLI

```powershell
codex plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
codex plugin add serial-monitor@serial-monitor-tools
```

설치 후 데스크톱 앱을 재시작하거나 CLI에서 새 세션을 시작합니다.
Codex IDE 확장은 [직접 MCP 등록](ai_control.md#수동-mcp-등록-호환-경로)을 사용하세요.

### Claude Code

```powershell
claude plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
claude plugin install serial-monitor@serial-monitor-tools --scope user
```

Claude Code 확장에서는 대화의 `/plugin` 메뉴를 사용할 수 있습니다.
설치 후 `/reload-plugins` 또는 새 세션에서 사용합니다.

### GitHub Copilot CLI

```powershell
copilot plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
copilot plugin install serial-monitor@serial-monitor-tools
```

설치 후 `/restart` 또는 새 세션에서 사용합니다.

### GitHub Copilot의 VS Code 환경

`chat.plugins.enabled`를 활성화한 뒤 Copilot 대화에서 각 줄을 실행합니다.

```text
/plugin marketplace add https://github.com/Establers/custom-serial-monitor.git
/plugin install serial-monitor@serial-monitor-tools
```

플러그인 관리 UI로 설치했다면 업데이트도 같은 UI에서 관리하세요.
Copilot CLI에 설치한 플러그인도 VS Code에서 확인할 수 있습니다.

## 연결 확인

새 AI 세션에 다음 요청을 붙여넣습니다.

> Serial Monitor 플러그인의 지원 기능과 실행 중인 GUI 목록을 조회해줘.
> 대상 GUI의 연결 상태와 저장 명령을 알려주고, 장치에 명령을 보내거나 연결을 바꾸지는 마.

여러 GUI가 실행 중이면 사용할 인스턴스를 먼저 선택합니다. 장치 포트가 연결되지
않았어도 기능·GUI 상태 조회는 가능합니다. 실제 제어에는 대상 포트와 제품 명령 문서를
제공하세요. 전송 완료만으로 제품 성공을 판단하지 말고 실제 RX를 확인해야 합니다.

## 업데이트

프로그램은 인스톨러로, 플러그인은 아래 명령으로 업데이트합니다.

```powershell
# Codex: 원본 갱신 후 설치 상태 확인·앱 재시작·새 세션
codex plugin marketplace upgrade serial-monitor-tools
codex plugin list

# Claude Code: 갱신 후 /reload-plugins 또는 새 세션
claude plugin update serial-monitor@serial-monitor-tools

# Copilot CLI: 갱신 후 /restart 또는 새 세션
copilot plugin update serial-monitor@serial-monitor-tools
```

VS Code Copilot은 `/plugin update serial-monitor@serial-monitor-tools` 또는
설치에 사용한 관리 UI의 Update를 사용합니다. 마켓플레이스 목록 새로고침과 설치한
플러그인 업데이트는 도구마다 다르므로, 반영된 버전을 확인하세요.

| 변경 내용 | 사용자 작업 |
| --- | --- |
| GUI 개선·제어 기능 수정·호환되는 새 MCP 도구 | 프로그램 업데이트 후 GUI 실행·MCP 재연결 |
| AI 작업 절차·skill 변경 | 플러그인 업데이트 후 재로드·새 세션 |
| 버전 호환 오류 | 안내된 프로그램 또는 플러그인을 업데이트 |

프로그램과 플러그인의 버전 번호는 같을 필요가 없습니다. 현재 플러그인은 **v1.0.2**이며
CLI v1.6.0 이상과 제어 API 1.x를 사용합니다. 최근 CLI·MCP 입력 검증, 취소·종료,
포화 상태의 취소 경로, 원래 연결 세션에 묶인 TX, 캡처 페이지와 다중 바이트 문자
경계를 지키는 시퀀스 기대값 검사는 **프로그램 v1.6.2**에 포함됩니다. 최소 호환 버전을
충족해도 수정사항까지 설치된 것은 아니므로 GUI와 CLI를 함께 v1.6.2 이상으로
업데이트하세요. `serial_get_capabilities` 또는
`serialctl describe --json`으로 CLI의 버전·지원 기능을 확인할 수 있습니다.
실행 중인 구버전 GUI는 새 GUI로 다시 실행하세요.

프로그램 설치 전에 진행 중인 수집·시퀀스를 마치고 GUI와 MCP 연결을 종료합니다.
설치 후 GUI를 실행하고 MCP를 다시 연결하세요.

단독 skill을 사용한다면 프로그램 업데이트만으로 바뀌지 않습니다. 새
`SerialMonitorSkills.zip`의 설치 스크립트를 기존과 같은 클라이언트·범위·경로로
다시 실행하세요. `serial-monitor.local.json`이나 수동 MCP의 실행 경로가 예전
`artifacts`/빌드/설치 폴더를 가리키는지도 확인합니다. 새 MCP 연결에서
`serial_get_capabilities`의 `data.cliVersion`을 확인한 뒤 `serial_list_instances`와
`serial_get_status`로 사용할 GUI를 조회하세요. CLI의 버전 조회만으로 실행 중인
GUI까지 새 버전으로 바뀌지는 않습니다. 연결 점검에 TX는 필요하지 않습니다.

개발 중에는 같은 v1.6.2 표기 안에서도 코드가 계속 바뀔 수 있습니다. 구현과 리뷰가
끝난 소스를 기준으로 GUI·CLI·플러그인·단독 skill 배포본을 함께 다시 만들고 검증하세요.
앞서 만든 설치 파일·ZIP은 이후 수정사항을 자동으로 포함하지 않습니다. 실행 중인
장치 작업 때문에 설치를 미뤘다면 스킬 내용만 갱신하고, 프로그램 교체·MCP 재연결은
그 작업을 마친 뒤 적용합니다. GUI와 CLI를 모두 갱신해야 일반 연결 포화 시의
취소 전용 경로와 세션 변경 보호를 함께 사용할 수 있습니다.

자동화 시퀀스의 공개 입력은 기존 TX 전용 `captureMs`/리터럴 `expect` 계약을 유지합니다.
새 GUI RX Check는 별도 v3 GUI JSON으로 불러오며, 활성 RX Check를 포함한 정의의
CLI/MCP 조회·실행은 `unsupported_sequence_feature`로 거절합니다. 이를 우회하려고
검사 조건을 지우거나 패턴을 장치 명령으로 보내지 마세요.

## 실행 파일을 찾지 못할 때

플러그인은 가까운 프로그램 폴더, 실행 중인 GUI, 설치 기록과 기본 경로 등에서
`serialctl.exe`를 찾습니다. 여러 위치의 프로그램이 실행 중이거나 포터블 경로를
명시해야 한다면 `SERIAL_MONITOR_CLI`를 설정하고, AI 호스트가 이 변수를
플러그인의 stdio MCP 프로세스에 전달하는지 확인하세요.

```powershell
$env:SERIAL_MONITOR_CLI = 'D:\Apps\SerialMonitor\tools\serialctl.exe'
# 사용하는 AI 호스트를 같은 창에서 시작
```

이 설정은 현재 PowerShell에서 시작하는 자식 프로세스에 적용됩니다. 이미 실행 중인
데스크톱 앱에는 전달되지 않으므로 호스트의 환경 설정을 적용하고 앱을 다시 실행하세요.

호스트가 MCP의 환경 변수를 필터링하면 앱을 재시작해도 위 변수만으로는 경로를
지정할 수 없습니다. Codex CLI 0.164.0-alpha.11.1에서 이 제한을 확인했습니다.
이 경우 호환되는 프로그램을 설치해 자동 탐색을 사용하거나,
[직접 MCP 등록](ai_control.md#수동-mcp-등록-호환-경로)으로 실행 파일의 절대 경로를
지정하세요. 플러그인 캐시의 파일을 직접 수정하면 업데이트 시 사라질 수 있습니다.

## 기존 skill·MCP에서 전환

플러그인을 설치하고 읽기 전용 연결 테스트를 마친 뒤, 대체한 기존 skill·수동 MCP 등록만
해당 AI 도구에서 비활성화하거나 제거합니다. 개인 수정본은 보존하세요.
플러그인 설치가 기존 설정을 자동으로 삭제하지는 않습니다.
플러그인을 지원하지 않는 환경은 [단독 skill](https://github.com/Establers/custom-serial-monitor/blob/main/docs/agent_skills.md)이나
[직접 MCP 등록](ai_control.md#수동-mcp-등록-호환-경로)을 유지할 수 있습니다.

<a id="share"></a>

## 공유·개발·오프라인 설치

다른 사람에게는 프로그램 다운로드 경로와 이 가이드를 전달합니다.
Git 설치 명령은 플러그인 파일이 저장소의 배포 브랜치에 게시된 뒤 사용할 수 있습니다.

오프라인·게시 전 개발본은 `<프로그램 폴더>\agent-plugin` 또는
`SerialMonitorPlugin.zip`을 푼 `SerialMonitorPlugin` 폴더를 사용할 수 있습니다.
해당 도구의 `plugin marketplace add` 뒤에 GitHub 주소 대신 로컬 원본 경로를 지정합니다.
같은 마켓플레이스 이름으로 Git·로컬 원본을 동시에 등록하지 않습니다.
로컬 원본은 파일 교체 후 호스트의 재로드·재시작으로 반영합니다.

배포 파일은 다음 명령으로 준비·검증합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package_agent_plugin.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test_agent_plugin.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check_release_payload.ps1 -PortableDirectory release\SerialMonitorPortable
```

원본 skill은 `skills/serial-monitor-control`이며 패키징이
`plugins/serial-monitor/skills/serial-monitor-control`에 동기화합니다. Git 배포에는
생성된 skill과 manifest·마켓플레이스 목록을 함께 반영하고 새 플러그인 버전을 사용하세요.
Codex 원본에는 `.agents/plugins/marketplace.json`, Claude Code·Copilot 호환 원본에는
`.claude-plugin/marketplace.json`을 함께 제공합니다. ZIP·프로그램 번들에도 둘 다
포함됩니다. 설치 파일 생성 전 검증은 GUI·CLI·인스톨러 버전과 실제 번들의
플러그인 파일·CLI 호환성을 검사하며, `-SkipPublish`에서도 오래된 번들을 거부합니다.
이 검사는 버전과 패키지 사본의 일치를 확인합니다. 같은 버전 안의 C# 코드 변경은
최종 소스 확정 후 프로그램을 새로 빌드·publish하여 반영해야 합니다.
패키지 생성만으로 GitHub 릴리스나 공개 디렉터리에 게시되지는 않습니다.
클라이언트별 검증은 [수동 체크리스트](https://github.com/Establers/custom-serial-monitor/blob/main/docs/manual_test_checklist.md)를 참고하세요.

공식 문서: [Codex](https://developers.openai.com/plugins/build/plugins),
[Claude Code](https://code.claude.com/docs/en/discover-plugins),
[Copilot CLI](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-plugin-reference),
[VS Code](https://code.visualstudio.com/docs/agent-customization/agent-plugins).
