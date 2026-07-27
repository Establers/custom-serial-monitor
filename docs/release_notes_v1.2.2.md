# Serial Monitor v1.2.2

출시일: 2026-07-27

## RS-485 HEX 버스 사용량 미터

- HEX 화면 상단에서 COM 포트로 정상 수신된 바이트를 기준으로 최근 60초의
  `RX BUSY`와 `IDLE` 비율을 확인할 수 있습니다.
- 현재 baud, data bits, parity, stop bits를 반영해 문자당 wire bit 수와 추정
  점유 시간을 계산합니다.
- 완전한 60초 측정 창이 준비되면 같은 창 안의 고정 1초 버킷 중 가장 높은
  점유율을 `PEAK`로 표시합니다.
- 연결 성공, Terminal에서 HEX 진입, HEX 화면 Clear를 새 측정 기준점으로
  사용하며 Terminal로 나가거나 연결을 해제하면 기존 측정값을 폐기합니다.
- 수신 콜백에는 추가 작업을 넣지 않고 기존 누적 RX 카운터를 1초마다 읽으며,
  메모리에 보관하는 표본 수를 제한했습니다.

## xterm 글꼴 설정

- Settings에서 xterm 로그의 글꼴과 10~15px 크기를 즉시 변경할 수 있습니다.
- Consolas를 기본값으로 사용하며 번들 JetBrains Mono와 Windows 설치 글꼴을
  선택할 수 있습니다.
- 선택한 글꼴과 크기는 JSON 프로필에 저장되고 다시 불러올 때 복원됩니다.
- 번들 글꼴이 아직 준비되지 않았거나 선택한 시스템 글꼴을 사용할 수 없으면
  안전한 monospace 대체 글꼴을 사용합니다.

## 측정 한계

- 버스 미터는 전기 신호를 직접 측정하지 않고 Windows COM 계층에서 성공적으로
  관측한 RX 바이트로 계산한 추정값입니다.
- 충돌, framing/parity 오류, driver overrun 등으로 유실된 wire activity는 복원할
  수 없습니다.
- 로컬 TX는 합산하지 않지만 어댑터가 송신 echo를 RX로 돌려주면 관측값에 포함될
  수 있습니다.

## 검증

- Release 구성에서 Core 테스트 35개와 WinUI 테스트 202개를 통과했습니다.
- 포터블 ZIP과 Inno Setup 설치 파일을 Release 구성으로 생성하고 필수 자산과
  SHA-256 체크섬을 확인했습니다.

## 설치 안내

- 설치 파일과 관리자 권한이 필요 없는 포터블 ZIP을 함께 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자
  경고가 표시될 수 있습니다.
- xterm 로그 화면을 사용하려면 Microsoft Edge WebView2 Runtime이 필요합니다.
