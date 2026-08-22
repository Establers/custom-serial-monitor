# Serial Monitor v1.3.3

출시일: 2026-08-22

## 장시간 화면 안정성

- xterm append가 밀린 동안 새 UI batch를 계속 쌓지 않고, bounded buffer의 최신
  상태를 한 번만 전체 렌더링하도록 변경했습니다.
- WebView2 스크립트 실행에 30초 timeout을 적용해 멈춘 호출이 UI 작업을
  무기한 붙잡지 않도록 했습니다.
- HEX 로그 buffer가 가득 찬 뒤 오래 실행될 때 trim 경계마다 전체 화면 로그를
  다시 만들지 않도록 개선했습니다. 분할 수신 그룹의 표시 경계는 유지합니다.

## 사용성

- TX 전송이 끝나면 Terminal 화면을 최신 로그로 이동합니다.
- 명령 시퀀스 반복 횟수 상한을 99회에서 9,999회로 늘렸습니다.

## 검증과 호환성

- Release 테스트 368개 통과(Core 35개, WinUI 333개).
- Windows 10/11 x64용이며 xterm 로그 화면에는 Microsoft Edge WebView2 Runtime이
  필요합니다.
- 실제 COM 장치와 72시간 soak 테스트는 별도 검증이 필요합니다.

## 설치 안내

- 일반 설치용 Inno Setup 실행 파일과 관리자 권한이 필요 없는 포터블 ZIP을
  제공합니다.
- 두 패키지 모두 SHA-256 체크섬 파일을 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자
  경고가 표시될 수 있습니다.
