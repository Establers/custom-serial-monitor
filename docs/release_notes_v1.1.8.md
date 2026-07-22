# Serial Monitor v1.1.8

출시일: 2026-07-23

## 검색 탐색 개선

- 검색창에서 Enter를 반복해서 누르면 현재 결과를 유지한 채 다음 결과로
  이동합니다.
- Shift+Enter는 검색 결과를 갱신한 뒤 이전 결과로 이동합니다.
- 로그가 추가되어 검색 결과의 위치가 바뀌어도 안정적인 로그 ID를 기준으로
  현재 위치를 복원합니다.

## HEX timeout 및 도움말

- 새 프로필의 자동 HEX grouping timeout을 baud와 관계없이 40 ms로
  적용합니다.
- 사용자가 명시적으로 저장한 custom timeout은 기존 값 그대로 유지합니다.
- Help/Guide를 섹션 형태로 정리하고 FTDI Latency Timer와 HEX timeout 조정
  안내를 눈에 잘 띄도록 추가했습니다.
- Terminal/HEX 통합 Mode, 명령 시퀀스, 검색 단축키 설명을 현재 동작에 맞게
  갱신했습니다.

## 검증

- Debug와 Release 구성에서 WinUI 테스트 164개와 Core 테스트 35개를 각각
  통과했습니다.
- 포터블 ZIP과 Inno Setup 설치 파일을 Release 구성으로 생성했습니다.

## 프리릴리스 안내

- 실제 USB-UART 어댑터별 장시간 검증이 남아 있어 프리릴리스로 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자
  경고가 표시될 수 있습니다.
