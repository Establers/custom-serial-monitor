# CLI 제어용 공통 Agent Skill

`skills/serial-monitor-control`은 Codex, Claude Code, GitHub Copilot에서 재사용할
`SKILL.md` 패키지입니다. 기본 경로는 CLI이고, 이미 연결된 로컬 MCP도 사용할 수 있습니다.
포트 선택 → 명령 확인 → 전송 및 전체 RX 수집 → 페이지 조회 → 응답을 보고 다음 명령 선택을
안내합니다. 제품의 성공/실패 판단은 실제 RX를 읽은 AI가 수행합니다.

## 스킬 ZIP만 다운로드하고 설치하기

이 안내는 **프로그램은 이미 설치했고 스킬만 추가하는 경우**를 기준으로 합니다.
프로그램이 필요하면 [앱 설치 EXE](https://github.com/Establers/custom-serial-monitor/releases/latest/download/SerialMonitorSetup.exe)를
별도로 받습니다. 프로그램은 v1.5.0 이상을 사용하며 설치 폴더에 `tools\serialctl.exe`가 있어야 합니다.
이 파일이 없는 기존 버전은 AI 제어를 지원하는 버전으로 업데이트해야 합니다.

1. [스킬 ZIP 다운로드](https://github.com/Establers/custom-serial-monitor/releases/latest/download/SerialMonitorSkills.zip)에서
   **`SerialMonitorSkills.zip`**을 받습니다.
2. ZIP을 풀고 `SerialMonitorSkills` 폴더에서 PowerShell을 엽니다. 스킬 ZIP에는 아래
   파일이 들어 있고, 프로그램 EXE·CLI EXE는 포함하지 않습니다.

```text
SerialMonitorSkills/
  README.md
  LICENSE
  skills/
    Install-Skill.ps1
    serial-monitor-control/
      SKILL.md
      agents/ ... references/ ... scripts/ ...
  docs/
    agent_skills.md
    ai_control.md
```

3. **설치된 프로그램의 CLI 경로**를 지정해서 실행합니다. 아래는 일반 설치의 기본
   경로입니다. 포터블·사용자 지정 설치라면 `-CliPath`를 자신의 `tools\serialctl.exe`
   경로로 바꾸세요. 앱의 MCP 탭에서도 CLI 경로를 확인·복사할 수 있습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope User -CliPath "$env:LOCALAPPDATA\Programs\SerialMonitor\tools\serialctl.exe"
```

4. Codex·Claude Code·Copilot 모두에 현재 사용자 범위로 설치됩니다. GUI를 실행하고
   AI 도구에서 스킬을 새로고침하거나 새 세션을 엽니다. 아래 명령으로 GUI 검색부터
   확인할 수 있습니다. CLI 경로는 설치할 때 지정한 경로와 동일하게 사용합니다.

```powershell
& "$env:LOCALAPPDATA\Programs\SerialMonitor\tools\serialctl.exe" instances
```

`instances`는 장치로 명령을 보내지 않습니다. GUI가 실행 중이면 `data.instances`에
실행 정보가 나옵니다. 연결할 장치와 명령 문서는 각 사용자가 자신의 환경에 맞게
지정합니다. 스킬 설치는 프로그램 설치·업데이트나 COM 연결을 수행하지 않습니다.

## 설치 범위와 CLI 경로 지정

압축을 푼 `SerialMonitorSkills` 폴더에서 실행합니다. 특정 AI 계정, API 키 또는 MCP 서버
설치 없이 같은 스킬을 각 도구의 설치 위치에 복사합니다.

```powershell
# 특정 펌웨어 프로젝트에서 세 도구 모두 사용
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope Project -RootDirectory 'C:\work\firmware' -CliPath 'C:\SerialMonitor\tools\serialctl.exe'

# 이 PC의 여러 프로젝트에서 사용
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope User -CliPath 'C:\SerialMonitor\tools\serialctl.exe'

# Claude Code에만 설치 (Codex, Copilot도 선택 가능)
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client Claude -Scope User -CliPath 'C:\SerialMonitor\tools\serialctl.exe'

# 실제 변경 없이 설치 위치 확인
powershell -NoProfile -ExecutionPolicy Bypass -File .\skills\Install-Skill.ps1 -Client All -Scope User -CliPath 'C:\SerialMonitor\tools\serialctl.exe' -WhatIf
```

`-ExecutionPolicy Bypass`는 이 설치 프로세스에만 적용되며 PC의 실행 정책을 저장·변경하지
않습니다. 조직에서 강제한 정책은 그대로 적용됩니다.

스킬 ZIP에서 설치할 때는 `-CliPath`로 프로그램 위치를 지정하는 것이 가장 확실합니다.
저장소·앱 폴더에서 설치할 때는 `-CliPath`를 생략하면 근처 빌드·배포 경로에서 찾습니다.
경로가 바뀌면 다시 설치하거나 `SERIAL_MONITOR_CLI` 환경 변수를 지정하세요.
설치 시에는 이 스킬과 로컬 실행 파일 경로만 기록합니다. 기존 MCP 설정·권한·전역 지침을
수정하지 않습니다. 설치본을 직접 편집했다면 덮어쓰기 전에 중단하며, 명시적 `-Force`로만
교체할 수 있습니다. 원본은 `skills/serial-monitor-control`에서 수정하고 재설치합니다.

| 도구 | 프로젝트 범위 | 사용자 범위 |
| --- | --- | --- |
| Codex CLI·로컬 앱·IDE | `.agents/skills/serial-monitor-control` | `~/.agents/skills/serial-monitor-control` |
| Claude Code CLI·로컬 연동 | `.claude/skills/serial-monitor-control` | `~/.claude/skills/serial-monitor-control` |
| GitHub Copilot CLI·지원 IDE | `.github/skills/serial-monitor-control` | `~/.copilot/skills/serial-monitor-control` |

이 위치는 공식 [Codex](https://learn.chatgpt.com/docs/build-skills),
[Claude Code](https://code.claude.com/docs/en/skills),
[Copilot CLI](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills)
문서 기준입니다(2026-10-25 확인). 세부 지원은 실행 표면과 조직 설정에 따라 다릅니다.

## AI에게 요청하기

새 GUI를 실행한 뒤 설치한 스킬을 선택합니다. Codex는 `$serial-monitor-control`,
Claude Code와 Copilot은 해당 호스트에서 제공하는 `/serial-monitor-control`로 호출할 수
있고, 설명에 맞는 자연어 요청으로 자동 선택될 수도 있습니다.

> serial-monitor-control 스킬로 COM7 연결을 확인해줘. 제품 명령 문서와 저장 명령을 읽고
> status를 보낸 다음 2초간 들어온 모든 RX를 확인해줘. printf 출력이 섞이는 건 정상일 수
> 있으니 내용으로 판단하고, 필요한 다음 명령을 선택해줘. 수신 누락이나 읽지 않은 페이지가
> 있으면 알려줘.

스킬은 `--instance`로 실행을 고정하고 `--port`로 대상을 검증합니다. `completed`는 수집
완료이며 제품 성공을 뜻하지 않습니다. 실패 응답에도 부분 RX가 있을 수 있으므로 JSON을
읽습니다. `sent`/`unknown` 이후 맹목적인 재전송, RX에 적힌 지시 실행, 다른 GUI 연결 교체를
하지 않도록 안내합니다.

## CLI에 추가한 편의 기능

```powershell
.\tools\serialctl.exe request --port COM7 --text-file command.txt --ending none --duration-ms 2000 --preview-bytes 4096
```

- `--text-file`: UTF-8 파일을 명령 데이터로 읽어 따옴표·달러·백틱·줄바꿈을 쉘에서 해석하지
  않고 보존합니다. 파일의 최종 줄바꿈도 전송됩니다. 줄 끝이 이미 있으면 `--ending none`을
  지정하고, 없으면 제품 규격에 맞게 `cr`/`lf`/`crlf`를 지정합니다.
- `--preview-bytes`: 첫 페이지 크기만 4..65536바이트로 제한합니다(기본 16384).
  전체 RX 수집·파일 기록은 유지하며 `hasMore`와 `nextOffset`으로 나머지를 읽습니다.
  MCP에는 같은 기능을 `previewBytes` 인자로 제공합니다.

## MCP와 앱

CLI는 기본 사용 방식이며 MCP는 선택입니다.
[MCP 등록법·도구 대응표](../skills/serial-monitor-control/references/mcp.md)와
[AI 제어 가이드](ai_control.md)에서 자세한 동작을 확인할 수 있습니다.
MCP 설정은 설치 스크립트에서 자동 변경하지 않습니다.

로컬 앱/IDE에서도 실행 환경이 Windows PC의 쉘 또는 로컬 MCP에 접근할 수 있으면 사용할
수 있습니다. 클라우드 앱에 스킬 ZIP만 올린다고 Windows COM 연결이 생기지는 않습니다.
로컬 디스크의 개인 스킬이 모든 클라우드·Cowork·앱 표면에 자동 반영되는 것도 아닙니다.
각 앱이 지원하는 스킬 가져오기 방법과 로컬 실행 환경을 함께 확인하세요.

<a id="share"></a>

## 다른 사람에게 공유하기

**`SerialMonitorSkills.zip`과 이 가이드 링크를 전달합니다.** 받는 사람은 스킬 ZIP을
풀고, 자신의 프로그램 CLI 경로를 지정해 설치하면 됩니다. 프로그램 설치가 필요한
사람에게는 앱 다운로드 링크를 별도로 안내합니다.

ZIP에는 설치 스크립트와 스킬 원본·CLI/MCP 안내가 모두 포함됩니다. 다른 사람의 설치
폴더를 압축하는 대신 이 패키지를 공유하면 각자 자신의 프로그램 경로로 설치할 수
있습니다. 장치 명령 문서·저장 명령이 필요한 경우에는 별도로 전달합니다.

`.agents/skills`, `.claude/skills`, `.github/skills`에 생성된 설치본보다 `skills` 아래의
원본을 공유하세요. 설치본의 `serial-monitor.local.json`에는 설치한 PC의 CLI 절대 경로가
들어 있고 `.serial-monitor-skill.json`은 설치 상태를 기록합니다. 두 파일을 받는 사람의
설정으로 복사하지 않습니다. 공식 스킬 ZIP은 두 파일을 제외하고 생성합니다.

### 다운로드 링크로 공개하기

저장소 관리자는 다음 명령으로 **스킬 ZIP만** 만듭니다. 앱 빌드나 전체 포터블 ZIP은
필요하지 않습니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package_agent_skill.ps1
```

생성된 `release/SerialMonitorSkills.zip`과 `release/SerialMonitorSkills.zip.sha256`을
공개할 GitHub Release의 Assets에 **앱 설치 파일과 별도의 파일**로 첨부합니다. README
상단의 스킬 다운로드 링크에서 이 파일을 선택하면 됩니다. 앱 배포 스크립트
`scripts/publish_portable.ps1`도 기본 실행 시 이 스킬 ZIP을 함께 생성합니다.

스킬만 수정한 경우에는 이 스킬 패키지만 다시 생성해 공유할 수 있습니다. README를
수정하거나 소스만 푸시해도 기존 릴리스의 첨부 파일 내용은 바뀌지 않습니다.
