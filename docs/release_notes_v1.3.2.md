# Serial Monitor v1.3.2

출시일: 2026-08-18

## 장시간 로깅 안정성

- 파일 writer의 큐를 레코드 100,000개와 UTF-8 기준 64 MiB로 제한하고,
  100개 단위 또는 2초 deadline으로 flush하도록 정비했습니다.
- Accepted, Durable, Uncertain, Abandoned 카운터를 분리해 수락·flush 완료·
  완료 여부 불확실·복구 실패 데이터를 구분합니다.
- write/flush 실패 시 현재 batch를 새 segment에서 최대 3회까지 복구하고,
  파일 시스템이 멈춘 경우 directory setup, open, write, flush, close에 timeout과
  late I/O 격리를 적용합니다.
- 파일 logging 상태를 Starting, Running, Stopping, Faulted로 표시하고
  파일 손실 카운터와 오류를 상태/Health에 노출합니다.

## 연결 수명주기 안정성

- disconnect/reconnect마다 독립 receive session과 RX channel을 사용해 이전
  수신 worker가 새 연결로 데이터를 섞지 않도록 했습니다.
- 연결 직후 실제 상태를 확인한 뒤에만 log pipeline과 자동 재연결 상태를
  시작하도록 했습니다.

## 검증과 호환성

- Release 빌드 경고 0개.
- Core 테스트 35개와 WinUI 테스트 329개, 총 364개 통과.
- Windows 10/11 x64용이며 xterm 로그 화면에는 Microsoft Edge WebView2 Runtime이
  필요합니다.
- 실제 COM 장치와 72시간 soak 테스트는 별도 검증이 필요합니다.

## 설치 안내

- 일반 설치용 Inno Setup 실행 파일과 관리자 권한이 필요 없는 포터블 ZIP을
  제공합니다.
- 두 패키지 모두 SHA-256 체크섬 파일을 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자
  경고가 표시될 수 있습니다.
