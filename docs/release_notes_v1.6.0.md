# v1.6.0 변경 사항

## Serial Monitor Plugin

- GUI의 MCP 메뉴를 **Plugin**으로 바꾸고 Codex 데스크톱·CLI, Claude Code,
  GitHub Copilot CLI·VS Code의 플러그인 설치·갱신 안내를 제공합니다.
- `serial-monitor` 플러그인은 공통 `serial-monitor-control` skill과 로컬 MCP launcher를
  함께 제공합니다. 마켓플레이스 이름은 `serial-monitor-tools`이며 기존 저장소에서 관리합니다.
- 설치·포터블 프로그램에 `<프로그램 폴더>\agent-plugin` 로컬 원본을 포함하고,
  앱 바이너리가 없는 `SerialMonitorPlugin.zip`도 제공합니다. 프로그램 설치기가 AI
  클라이언트 설정이나 설치 캐시를 자동으로 덮어쓰지는 않습니다.
- launcher는 현재 CLI를 찾아 호환성을 확인한 후 MCP를 실행합니다. 일반 설치의
  사용자 지정 경로, 가까운 포터블 프로그램, `SERIAL_MONITOR_CLI` 명시 경로를 지원합니다.

## 기능 조회와 업데이트

- `serialctl describe --json`과 MCP `serial_get_capabilities`는 CLI/server 버전,
  제어 API, 최소 플러그인 버전, 지원 기능과 실제 도구 스키마를 제공합니다.
  이 조회는 GUI나 COM 포트를 열지 않습니다.
- skill은 연결된 MCP 도구를 우선 사용하고 CLI를 대안으로 지원합니다. 새 기능의
  인자는 실행 중인 서버에서 조회하며, 번들 문서를 모든 이후 버전의 고정 명령 목록으로
  간주하지 않습니다. 이미 실행 중인 구버전 GUI는 별도 업데이트가 필요할 수 있습니다.
- 프로그램과 플러그인의 버전은 별도로 관리합니다. 로컬 원본은 인스톨러 설치 후
  클라이언트별 재로드·재시작으로, Git 원본은 해당 도구의 갱신 기능으로 적용합니다.

## 기존 설치에서 전환

- `SerialMonitorSkills.zip`과 직접 CLI/MCP 등록은 호환 경로로 유지합니다.
  기존 단독 skill·수동 서버 등록·개인 수정본을 자동으로 삭제하지 않습니다.
- 플러그인 설치 후 읽기 전용 조회로 연결을 확인하고, 대체한 중복 항목만 해당
  AI 도구의 설정에서 의도적으로 정리하세요.
- 시리얼 RX/TX와 로그 파이프라인의 소유권·큐·제어 예약 구조는 유지합니다.
  플러그인은 장치 명령을 자동으로 전송하거나 제품 성공을 전송 완료만으로 판단하지 않습니다.

## 배포 파일

- `SerialMonitorSetup.exe`: 일반 프로그램 설치
- `SerialMonitorPortable.zip`: 프로그램과 로컬 플러그인 원본
- `SerialMonitorPlugin.zip`: 별도 플러그인 원본
- `SerialMonitorSkills.zip`: 기존 단독 skill 호환 설치

자세한 절차는 [Plugin 가이드](agent_plugins.md)와
[수동 설치·업데이트 체크리스트](manual_test_checklist.md)를 참고하세요.
저장소 변경과 패키지 생성만으로 GitHub 릴리스나 공개 플러그인 디렉터리가 게시되지는 않습니다.
