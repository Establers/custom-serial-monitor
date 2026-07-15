# Serial Monitor v1.1.4

출시일: 2026-07-15

## 로그 표시 정리

- HEX 송신 로그에서 `[HEX]` 접두사를 제거해
  `TX > 12 34`처럼 전송 바이트만 표시합니다.
- 가상 COM 포트에서 들어온 브리지 데이터를 `RX`로 표시하고
  `[BRIDGE]` 접두사를 제거했습니다.
- 브리지 수신 데이터는 Terminal/HEX 모드에 맞는 `RxOnly` 이벤트·
  하이라이트·필터 규칙을 사용합니다.

## MOCK 표시 정리

- MOCK 응답에서 `mock device received command/bytes` 문구를 제거했습니다.
- MOCK 브리지 응답에도 전송 페이로드만 표시하여 실제
  시리얼 수신 로그와 같은 형태로 확인할 수 있습니다.

## 검증

- Core 테스트 35개와 WinUI 테스트 113개, 총 148개를 통과했습니다.
- Release 빌드와 portable/installer 패키지 생성을 검증했습니다.
- `git diff --check`를 통과했습니다.

## 프리릴리스 안내

- 실제 USB-UART 어댑터별 장시간 검증이 남아 있어 프리릴리스로
  제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는
  게시자 경고가 표시될 수 있습니다.
