# 공용 제품 메모리 검증 — 2026-11-08

대상: `b43d99c`의 공용 Markdown 메모리와 `ad561d7`의 빠른 시작 하단 배치.
노트는 같은 Windows 사용자의 `%LOCALAPPDATA%\SerialMonitor\Notes`에 두고,
AI가 skill에서 위치를 배운 뒤 일반 파일 도구로 읽고 수정한다.

## 실제로 제공하는 안내

- 장치 작업 또는 대상 변경 시 INDEX와 관련 제품 노트를 읽는다.
- 제품명·모델/제품 코드, 별칭과 구분 정보를 기록한다. 이름 유사성만으로 합치지 않고 식별이 모호할 때만 확인한다.
- 재사용할 프로토콜 분석·테스트 패킷·확인 내용을 짧은 코드/캡처 근거와 함께 갱신한다.
- 최소 양식은 식별 정보 / 프로토콜·연결 제품 / 테스트·확인 내용이다. 문서 분할과 정리 방법은 에이전트가 정한다.

## 새 AI 세션의 독립 시나리오

실제 사용자 메모리와 UART를 사용하지 않는 별도 디렉터리 및 모의 CLI로 시험했다.
원본 노트 스냅샷과 실행 로그를 대조했으며, 첫 세 사례는 skill을 명시하고 마지막 사례는
skill의 이름·설명·위치만 제공했다. 명령 전 읽기 순서는 각 에이전트의 실행 기록으로 확인했다.
CLI 로그의 INDEX 해시는 파일이 호출 시점에 존재함을 확인하며, 그 해시만으로 읽기 자체를 증명하지는 않는다.

| 사례 | 관찰 결과 |
| --- | --- |
| 새 클론 + 별칭이 같은 제품 10개 | 코드의 TM-200B/B2와 일치하는 기존 노트 하나만 읽고 갱신. 다른 9개와 INDEX는 보존. 새 제품 중복 생성 없음. TX 1회. |
| 별칭만 있고 모델 식별 정보 없음 | 코드·연결 상태로 구분하지 못하자 모델/하드웨어를 질문. TX 0회, 노트 수정 0개. |
| 공용 노트가 없는 첫 사용 | 초기 파일을 만든 뒤 PW-100/R1 제품 문서와 INDEX 연결 생성. TX 1회. 모의 결과를 실측 하드웨어 결과로 표시하지 않음. |
| skill을 명시적으로 호출하지 않은 새 세션 | 제공된 skill 메타데이터로 가이드를 선택하고 기존 TM-200B 노트 재사용. TX 1회, 다른 제품 보존. |

모의 CLI의 최초 구현은 sequence get을 생략해 빈 목록을 반환했다. 이를 실제 앱과 같은
정의 반환 형태로 보완하고, 추가 TX·저장·실행 없이 세 사례의 기존 정의를 다시 읽었다.
각 반환 정의의 모든 필드/자료형/값이 저장한 JSON과 일치했고 총 TX 횟수는 각각 1회로 유지됐다.
이 제한은 시험 도구의 문제였으며 제품의 시퀀스 결함으로 판정하지 않았다.

## GUI와 파일 보존

별도 이름의 Debug 미리보기 빌드에서 Plugin → 빠른 시작 하단의 버튼을 확인하고 클릭했다.
탐색기의 경로와 항목 접근성 정보로 실제 `%LOCALAPPDATA%\SerialMonitor\Notes`가 열린 것을 확인했다.
기존 파일 2개의 SHA-256은 열기 전후 같았다. 다른 페이지를 포함한 추가 화면 조작은 사용자의
Escape 입력으로 중단했다. 모든 페이지의 시각적 회귀 검증이 끝났다는 의미는 아니다.

자동 검사는 기존 노트/사용자 양식 보존, 동시 초기화, 취소와 실패 복구를 다룬다.
추가한 실패 시험은 템플릿 경로를 디렉터리로 막아 게시 실패를 재현하고, 기존 INDEX/제품 노트와
임시 파일 정리를 확인한 뒤 장애 제거 후 정상 초기화까지 확인한다.

## COM3 실제 보드

실행 중인 GUI가 없어 현재 Release GUI를 시작하고 COM3를 연결했다.
ST-LINK Virtual COM Port, 115200 / 8N1 / handshake None, UTF-8, byte timeout 40ms였다.

- 실제 ID: `NUCLEO-F103RB STM32F103RB SERIAL-LAB/1`.
- 실제 BUILD: `version=1.6.0 protocol=1 id=A4764BF8CC60569C`.
- 최초 FC03 조회는 1초간 RX 0바이트. CRLF로 파서 경계를 정리한 ID 조회에서 제어 문자 오류 뒤 정상 ID가 왔다.
  초기 LAB 파서에 바이너리를 보낸 영향으로 판단했다. 무응답만으로 장치 고장으로 판정하거나 자동 재전송하지 않았다.
- `SELFTEST PASS crc=PASS fixtures=PASS limits=PASS`.
- `RTU` 진입 ACK 후 TX `01 03 00 00 00 02 C4 0B`에 RX `01 03 04 00 FA 00 64 DB E9`.
  PC에서 CRC16/Modbus와 레지스터 값 250/100을 독립 검산했다.
- `LAB` 복귀 ACK 후 `PING memory-20261108` → `PONG memory-20261108`.
- 마지막 STATUS: parse_errors=1, rx_drops=0, uart_errors=0, tx_rejected=0, 의도적 오류 주입 카운터 0.
- 7개 캡처 모두 sent/completed, 누락·시리얼 오류·RX gap·미확인 후속 페이지 없음.
  raw 파일 길이와 receivedBytes/storedBytes도 일치했다.

공용 INDEX와 `products/nucleo-f103rb-serial-lab/README.md`에 실제 근거를 저장했다.
같은 보드에 다른 펌웨어가 있는 경우를 구분하고, 관찰한 빌드와 현재 소스 후보 이미지의 동일성은 확정하지 않았다.

### 새 에이전트의 실제 메모리 재사용

공용 노트 내용을 프롬프트에 복사하지 않은 새 에이전트에 COM3 조회 작업과 skill 메타데이터를 제공했다.
설치된 skill → 실제 공용 INDEX → 기존 제품 README를 읽고, 연결 상태를 확인한 뒤
`STATUS?` + CRLF를 한 번만 전송했다. 2초 동안 RX 156바이트 전체를 확인하고 같은 README에 결과를 추가했다.
제품 항목이나 문서를 중복 생성하지 않았고 INDEX는 보존했다.

캡처 `42595bcd03474466bddebe39771fd8a2`는 sent/completed, 누락·시리얼 오류·RX gap 없음,
hasMore=false였다. 원본 파일과 반환 text 페이지도 대조했다. parse_errors=1은 이전 관찰과 같았다.
새 읽기 전용 시퀀스 `F103RB LAB - Read-only STATUS`를 덮어쓰기 없이 저장하고 정의를 재조회했다.
정확한 STATUS 명령, text/CRLF, captureMs=2000, repeatCount=1을 별도로 확인했으며 실행하지 않았다.
최종 COM3 연결은 유지했고 idle, sessionGeneration=1, serialErrors=0, LAB 상태였다.

## 설치된 가이드와 범위

현재 프로젝트의 `.agents/skills/serial-monitor-control/SKILL.md`에는 새 안내가 아직 없었다.
소유권 manifest와 일치하는 SKILL 및 새 메모리 assets만 갱신했다. 별도로 수정되어 있던
CLI/MCP 참고 문서와 기존 CLI pin은 해시를 확인해 보존했다. 전역 설정이나 플러그인 캐시는 수정하지 않았다.

이 방식은 가이드가 실제로 선택/로드되고 파일 접근이 가능한 환경을 전제로 한다.
임의의 CLI 직접 호출을 강제로 가로채는 기능은 아니며, Claude 등 각 실제 호스트의 자동 skill 선택을
모두 검증한 것은 아니다. 실제 UART 시험도 짧은 기능 시험이며 장기 안정성 시험은 아니다.

## 재실행 및 결과

```powershell
dotnet build SerialMonitor.WinUI\SerialMonitor.WinUI.sln -c Release
dotnet test SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~AgentMemoryStoreTests|FullyQualifiedName~PluginClientConfigurationTests"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test_agent_plugin.ps1 -CliPath <serialctl.exe 절대 경로>
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test_agent_skill.ps1
```

Release/미리보기 빌드는 경고·오류 0. 관련 자동 검사 18개, 플러그인 검사 22개,
단독 skill 설치·패키지 검사 11개가 통과했다. 로컬 상세 증거는
`artifacts/agent-memory-validation-20261108/`의 실행 기록·원본 스냅샷·behavior-summary.json·TRX·hardware JSON에 있다.
