# 도구 패널 이동

탭 줄 맨 앞의 **패널 이동** 손잡이를 드래그하여 도구 패널 전체를 왼쪽·오른쪽·하단에 붙인다.
드래그 중 표시되는 영역은 놓았을 때 패널이 차지할 위치다. 중앙이나 창 밖에서 놓거나,
Esc를 누르거나, 다른 창으로 전환하면 이동을 취소한다. 손잡이를 클릭하거나 키보드로
포커스를 옮긴 뒤 Enter를 누르면 같은 세 위치를 메뉴에서 선택할 수 있다.

로그와 패널 사이의 경계선은 크기 조절에 사용한다. 좌우 배치에서는 너비를, 하단 배치에서는
높이를 조절한다. 경계선을 두 번 클릭하면 접거나 펼친다. 접힌 상태에서도 경계선은 남는다.
하단 높이와 사이드 너비는 창이 열려 있는 동안 각각 기억하며, 앱을 다시 열면 하단 배치로 시작한다.
폭이 좁으면 Protocol 도구 모음을 두 줄로 배치하고, 시퀀스 도구 모음과 명령 표는 가로로
스크롤할 수 있다. 스크롤바가 버튼을 덮지 않도록 넘치는 영역에만 아래 여백을 둔다.

이동은 기존 화면의 Grid 위치만 바꾼다. 탭 선택, 입력 중인 값, 터미널 WebView와 데이터 모델을
새로 만들지 않는다. 송신·수신·파일 로깅의 실행 상태는 배치와 독립적이다.

## 검증

```powershell
dotnet build SerialMonitor.WinUI/SerialMonitor.WinUI.sln -c Release
dotnet test SerialMonitor.WinUI/SerialMonitor.WinUI.sln -c Release --no-build -m:1
.\scripts\test_modbus_layout_native.ps1 -AppPath .\SerialMonitor.WinUI\bin\x64\Release\net8.0-windows10.0.19041.0\win10-x64\SerialMonitor.WinUI.exe -CaseFilter inspector-docking
```

- 기하 검사: 좌우·하단·모서리 선택, 중앙·창 밖 취소, 반대 방향의 좌우 리사이즈, 최소 로그 공간.
- 실제 WinUI 자동 검사: 1200×800과 800×600에서 세 방향 배치, 모든 탭 전환, 기존 인스턴스와
  선택 유지, 경계선 접근, 접기·복원·크기 변경. 장치 연결이나 송신을 하지 않는다.
- 실제 입력 확인: 손잡이 드래그로 오른쪽 → 왼쪽 → 하단 이동, 중앙 드롭 취소, 위치 메뉴,
  좌우 경계선 크기 조절과 두 번 클릭 복원. Esc·창 밖 드롭·다른 창 전환과 여러 DPI도 확인한다.

장치 연결 중 장기 운전 결과는 UI 배치 검사와 별도로 기록한다.

## 2026-11-15 확인 결과

- 최종 Release 솔루션 빌드와 별도 Debug 미리보기 빌드: 경고·오류 0.
- 초기 구현의 전체 회귀: 3,239 통과, 장비·가상 COM 환경 의존 19건 건너뜀.
  좁은 폭 보완 후 최종 Inspector/Protocol/Modbus/Sequence/Shortcut 회귀: 981 통과, 환경 의존 4건 건너뜀.
  `artifacts/inspector-docking-tests/inspector-docking-final.trx`.
- 최종 실제 WinUI 검사: 8개 배치 시나리오, 각 11개 탭 및 Protocol의 네 가지 화면,
  미리보기·취소 정리·접기·복원·스크롤바 여백 확인, 문제 0·시리얼 송신 0 B.
  `artifacts/inspector-docking-native/e933e9dcf9124ca0a859c0711ce6b85f/result.json`.
- Computer Use로 실제 드래그의 오른쪽→왼쪽→하단 이동, 중앙 드롭 취소, 사이드 너비 조절,
  두 번 클릭 접기·복원, 위치 메뉴 이동, 최소 폭에서 시퀀스 스크롤 조작을 확인했다.
  마지막 보완 후 버튼 위로 스크롤바가 겹치지 않는 것도 확인했다.
- 드래그 도중 Esc·창 밖 드롭·다른 창 전환의 실제 입력 조합, 다른 DPI, 실장비 장기 운전은 미실행.
  취소의 좌표 판정과 공통 상태 정리 경로는 자동 검사에 포함했다.
