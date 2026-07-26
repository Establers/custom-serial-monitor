<div align="center">
  <img src="icon.PNG" width="96" alt="Serial Monitor icon" />
  <h1>Serial Monitor</h1>
  <p><strong>임베디드 장치의 오래 걸리는 버그를 놓치지 않는 Windows 시리얼 모니터</strong></p>
  <p>
    <a href="https://github.com/Establers/custom-serial-monitor/releases/latest"><img src="https://img.shields.io/github/v/release/Establers/custom-serial-monitor?display_name=tag&style=flat-square" alt="Latest release" /></a>
    <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&logo=windows" alt="Windows 10 and 11" />
    <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 8" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="MIT License" /></a>
  </p>
  <p>
    <a href="https://github.com/Establers/custom-serial-monitor/releases/latest"><strong>최신 버전 다운로드</strong></a>
    ·
    <a href="docs/release_notes_v1.2.1.md">v1.2.1 변경 사항</a>
  </p>
</div>

![MOCK 장치의 실시간 로그와 탐지된 이벤트를 함께 보여주는 Serial Monitor](docs/images/serial-monitor-overview.png)

Serial Monitor는 임베디드·RTOS 장치를 오래 연결해 두고 로그를 수집하거나,
간헐적으로 발생하는 오류를 재현할 때 쓰는 WinUI 3 데스크톱 앱입니다. 화면에는
최근 로그만 제한적으로 유지하고 전체 로그는 비동기로 디스크에 기록하므로, 빠른
데이터가 계속 들어오는 상황에서도 UI 응답성과 메모리 사용량을 안정적으로
유지하는 데 초점을 맞췄습니다.

## 이 앱이 유용한 이유

- **밤새 지켜보지 않아도 됩니다.** 키워드 규칙으로 WARN/ERROR 같은 이벤트를
  탐지하고, 발생 전·일치·발생 후 문맥을 함께 보관합니다.
- **문제가 난 순간을 빨리 찾습니다.** 화면에 남아 있는 로그를 검색하고
  Next/Prev 또는 검색 결과 목록에서 해당 위치로 바로 이동할 수 있습니다.
- **반복 테스트가 짧아집니다.** 자주 쓰는 TX 명령, 빠른 전송 버튼, 순차 명령과
  지연 시간을 프로필로 저장해 같은 검증 절차를 다시 실행할 수 있습니다.
- **분석 기록이 남습니다.** 사용자 마커를 로그 타임라인에 넣고 전체 세션을 일반
  텍스트 파일로 저장하므로, 재현 시점과 작업 메모를 함께 추적하기 쉽습니다.
- **현장 환경에 맞게 배포할 수 있습니다.** 설치형 EXE와 관리자 권한이 필요 없는
  포터블 ZIP을 함께 제공하며, MSIX를 사용하지 않습니다.

## 이런 분에게 잘 맞습니다

- UART 로그를 장시간 관찰하는 펌웨어·임베디드·RTOS 개발자
- 간헐적인 재부팅, 타임아웃, 경고를 재현하고 전후 로그가 필요한 QA 엔지니어
- 반복 명령과 결과를 같은 설정으로 남겨야 하는 장비 검증·현장 지원 담당자
- Terminal 텍스트와 HEX 패킷을 한 도구에서 오가며 확인하려는 개발자

## 5분 안에 시작하기

1. [Releases](https://github.com/Establers/custom-serial-monitor/releases/latest)에서
   설치 파일(`SerialMonitorSetup_*.exe`) 또는 포터블 ZIP을 받습니다.
2. 포터블 버전은 ZIP 전체를 푼 뒤 `SerialMonitor.WinUI.exe`를 실행합니다.
3. `Port`와 `Baud`를 선택하고 `Connect`를 누릅니다. 장치 없이 먼저 살펴보려면
   `Test` 탭에서 **Show MOCK test port**를 켜고 `[TEST] MOCK`에 연결합니다.
4. 기록이 필요하면 `Log` 탭에서 저장 위치와 회전을 설정한 뒤 `LOG ON`을
   사용합니다.
5. 자주 쓰는 명령, 이벤트 규칙, 하이라이트, 시퀀스를 만든 뒤 프로필로
   저장합니다.

> Windows 10/11 x64용입니다. xterm 로그 화면에는 Microsoft Edge WebView2
> Runtime이 필요합니다. 설치 파일은 현재 코드 서명이 없어 SmartScreen에서
> 알 수 없는 게시자 경고가 표시될 수 있습니다.

## 로그를 찾는 방식

![WARN 검색 결과를 시간, 방향, 메시지로 나누어 보여주는 Serial Monitor](docs/images/serial-monitor-search.png)

상단 검색은 현재 화면 메모리에 남아 있는 로그에서 일치 항목을 찾습니다. 검색
결과 패널은 시간·RX/TX 방향·메시지를 나눠 보여주며, 결과를 더블 클릭하면 원본
로그 위치로 이동합니다. 로그가 계속 들어오는 동안 선택이 흔들리지 않도록 결과
목록은 기본적으로 수동 새로고침 방식입니다. 디스크에 저장된 전체 로그 검색은
외부 텍스트 도구를 사용해야 합니다.

## 주요 기능

| 작업 | 제공 기능 |
| --- | --- |
| 수신·표시 | 실제 COM 포트, Terminal/HEX 모드, UTF 계열·코드 페이지 디코딩, xterm.js 선택/복사 |
| 기록 | 비동기 일반 텍스트 로그, 크기 기반 회전, 파일 이름 지정, bounded UI buffer |
| 탐지 | Terminal/HEX 이벤트 규칙, 전후 문맥 캡처, 하이라이트, 트레이 알림 |
| 전송 | 수동 TX, 줄 끝 설정, 저장 명령, 단축키, 명령 시퀀스, 사용자 마커 |
| 재사용 | 설정·규칙·명령·시퀀스를 JSON 프로필로 저장/불러오기/초기화 |
| 연동 | 물리 COM과 com0com 같은 가상 COM 사이의 선택적 양방향 raw-byte bridge |
| 진단 | MOCK 포트, 스트레스 생성기, 손실 카운터, 상태 요약, 복사 가능한 진단 정보 |

## v1.2.1

- 촘촘한 엔지니어링 UI에서도 버튼, 입력 필드, 탭의 상태와 경계를 더 분명하게
  보이도록 다듬었습니다.
- 검색 결과를 시간·방향·메시지 구조로 정리해 긴 목록을 더 빠르게 훑을 수
  있습니다.
- JetBrains Mono를 앱에 포함해 대상 PC의 설치 글꼴과 관계없이 터미널 정렬과
  가독성을 일정하게 유지합니다.
- Debug/Release 빌드와 포터블·설치 파일 생성 절차를 보강했습니다.

자세한 내용은 [v1.2.1 릴리즈 노트](docs/release_notes_v1.2.1.md)를 참고하세요.

## 개발 및 빌드

저장소 루트에서 다음 명령을 실행합니다.

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.csproj -p:Platform=x64
```

포터블 ZIP과 선택적 Inno Setup 설치 파일은 각각 다음 스크립트로 만듭니다.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish_portable.ps1
powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1
```

출력은 `release` 폴더에 생성되며 MSIX/AppX 패키지는 만들지 않습니다. 자세한
절차는 [포터블 배포](docs/portable_deployment.md)와
[설치 파일 배포](docs/installer_deployment.md)를 참고하세요.

## 데이터 위치

- 로그: `%LOCALAPPDATA%\SerialMonitor\logs`
- 기본 프로필: `%LOCALAPPDATA%\SerialMonitor\profiles\default.json`
- 마지막 런타임 오류: `%LOCALAPPDATA%\SerialMonitor\diagnostics\last_runtime_error.txt`

## 문서

- [기능 및 제약 사항](docs/known_limitations.md)
- [수동 회귀 테스트 체크리스트](docs/manual_test_checklist.md)
- [com0com 스트레스 테스트](docs/com0com_stress_testing.md)
- [아키텍처](docs/architecture.md)
- [로깅 동작](docs/logging.md)

## 라이선스

[MIT License](LICENSE)로 배포됩니다. 포함된 JetBrains Mono 글꼴은
`SerialMonitor.WinUI/Assets/xterm/fonts/OFL.txt`의 SIL Open Font License를
따릅니다.
