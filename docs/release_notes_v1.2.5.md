# Serial Monitor v1.2.5

출시일: 2026-08-03

## Bridge 패킷 그룹화와 UI 언어

- Bridge가 장치에서 받은 데이터를 개별 transport read 단위가 아니라 설정된
  idle gap까지 하나의 논리 패킷으로 모아 가상 포트에 전달합니다.
- 패킷 재생 순서와 경계를 보존하는 전용 그룹화·재생 경로를 추가했습니다.
- 주요 화면 문자열을 한국어와 영어 리소스로 분리해 Windows 표시 언어에 맞게
  보여 줍니다.
- Bridge 패킷 경계, gap replay, com0com 통합 동작을 자동 테스트로 검증했습니다.

## 자동 시리얼 재연결

- 정상 연결 후 실제 COM 포트의 읽기·쓰기 오류로 연결이 끊기면 같은 포트에
  자동으로 재연결합니다.
- 재시도 간격은 1초, 2초, 5초, 10초로 증가하며 이후에는 10초로 제한됩니다.
- 연결 툴바에서 재연결을 취소할 수 있고, 설정은 프로필에 저장됩니다.
- 재연결 중에는 파일 writer와 이벤트 detector를 유지하고 serial transport와 RX
  pipeline만 다시 구성합니다.
- Bridge와 실행 중이던 command sequence는 안전하게 중지하며 복구 후 자동으로
  재시작하지 않습니다.
- 재연결 상태, 시도·성공·실패 횟수와 마지막 오류를 진단 정보에 표시합니다.

## 선택 구간 시간차

- Terminal과 HEX 로그에서 선택한 첫 줄과 마지막 줄의 timestamp 차이를 tooltip로
  보여 줍니다.
- HEX 모드는 첫 줄의 byte 수를 유지하고 둘째 줄에 시간차를 표시합니다.
- drag 중 live append, auto-scroll, 표시 모드 동기화가 발생해도 tooltip이 선택을
  마칠 때까지 유지됩니다.
- 시간 계산용 숨은 metadata는 화면, 검색, 복사, 저장 로그의 원문을 바꾸지
  않습니다.

## 안정성과 호환성

- Serial RX, parsing, 비동기 파일 기록, 이벤트 탐지, UI 렌더링 사이의 기존
  bounded queue/channel 구조를 유지합니다.
- Release 구성에서 Core 테스트 35개와 WinUI 테스트 284개, 총 319개를
  통과했습니다.
- 자동 재연결은 장치 고유 ID를 추적하지 않으므로 COM 번호가 바뀐 경우에는 새
  포트를 수동으로 선택해야 합니다.
- Windows 10/11 x64용이며 xterm 로그 화면에는 Microsoft Edge WebView2 Runtime이
  필요합니다.

## 설치 안내

- 일반 설치용 Inno Setup 실행 파일과 관리자 권한이 필요 없는 포터블 ZIP을 함께
  제공합니다.
- 두 패키지 모두 SHA-256 체크섬 파일을 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자 경고가
  표시될 수 있습니다.
