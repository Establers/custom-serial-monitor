# 100만 줄 복원 반복 문제 — 2026-09-08

## 확인한 문제

- 실행 중인 Debug SerialMonitor 프로세스(PID 22160)의 working set 약 2.1GB를 확인했다. 창 상태 도구는 잘못된 설치 경로로 소유자를 판별해 두 번 실패했다. 따라서 사용자 앱 화면을 직접 읽거나 클릭했다고 주장하지 않는다. 앱을 종료하거나 이력을 지우지 않았다.
- 기존 RouteLiveXtermBatch는 5,000줄 soft backpressure에서 새 표시 배치를 버리고 전체 재그리기를 예약했다. 특히 전체 snapshot도 pending character 통계에 포함되어 복원 중 backpressure를 유발했다.
- acknowledgement recovery의 1초 재시도 타이머는 복원이 이미 진행 중이어도 다음 전체 복원을 예약했다. 성공 직후 같은 화면을 또 재구성할 수 있었다.
- 100만 줄 전체를 JS 큐에 먼저 복제한 다음 파싱했으며, 전체 완료에 일반 추가 배치와 동일한 30초 제한을 사용했다.
- 일반 전체 재그리기가 성공해도 isRestoreRender가 false이면 미복원 플래그가 남았다.

## 변경

- 실시간 대기 문자 수를 snapshot 통계와 분리했다. soft backpressure는 부가 스크롤 제어에 사용하고, 새 배치는 최대 32Mi 문자 또는 보관 행 수까지 bounded queue에 유지한다. 실제 초과 시에는 기존 snapshot 복구 경로를 사용한다. 수신/파일 저장을 UI 정체 때문에 의도적으로 중지하지 않는다.
- 복원 실행/예약 중에는 acknowledgement 재시도를 예약하지 않는다. 실제 실패 후 자동 재시도는 한 번으로 제한한다.
- 전체 snapshot은 한 번 clear한 뒤 최대 256Ki 문자씩 보내고 각 묶음의 파싱 완료를 기다린다. JS에 전체 snapshot 사본을 쌓지 않는다. 30초 제한은 각각의 작은 묶음에 적용된다.
- 소비한 JS 큐 항목은 즉시 null로 바꾸어 텍스트 참조를 반환한다.
- 어떤 경로에서 요청했든 snapshot 완료 시 미복원 상태를 해제한다.

## 검증

- WinUI 관련 테스트 354개, Core 테스트 35개 통과.
- JS 큐/압축 테스트 13개 통과. 여러 번 chunk commit을 해도 이전 chunk를 지우지 않고 이어 붙이는지, 소비한 큐 항목이 해제되는지 확인했다.
- 별도 headless Edge에서 실제 배포 HTML·xterm·OSC 메타데이터를 사용해 100만 줄을 streaming replacement로 복원했다. 약 12.03초에 완료한 뒤 복원 중 보류한 500줄을 추가했다. 마지막 줄 live 499, 대기열 0, writing=false, 메타데이터 100만 개, 소비된 chunk 참조 0, 페이지 오류 0이었다.
- 기존 브라우저 시험의 OSC fixture 구분자가 실제 프로토콜과 다르게 세미콜론으로 작성되어 있던 것을 쉼표로 수정했다. 새 100만 줄 복원 시험은 실제 파싱된 metadata 개수까지 검사한다.
- 별도 Release 출력 artifacts/recovery-fixed 빌드 오류 0, 경고 0. 실행 중인 Debug 출력은 덮어쓰지 않았다.

## 한계

12.03초는 별도 Edge 시험 결과다. 실행 중인 WinUI/WebView2 프로세스의 동일 시간을 측정한 것은 아니다. 지속 입력이 화면 처리량과 bounded queue를 계속 초과하면 전체 재동기화가 필요할 수 있다. 디스크 공간 및 event context drop 경고는 별도 상태이며 이번 변경으로 지워지지 않는다.

현재 실행 중인 프로세스에는 수정이 자동 반영되지 않는다. File OFF 상태의 메모리 이력을 보존하려면 종료 전에 필요한 로그를 별도로 보관해야 한다.

## 재실행

~~~powershell
node --test scripts/test_compact_scrollback.mjs scripts/test_xterm_queue.mjs
# Playwright 설치 환경(NODE_PATH 지정 가능)
node scripts/test_xterm_recovery_browser.cjs
dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj --no-restore -p:Platform=x64 -p:OutDir=C:\Users\pjh\Desktop\serial\artifacts\recovery-check\
~~~
