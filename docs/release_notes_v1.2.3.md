# Serial Monitor v1.2.3

출시일: 2026-07-29

## 로그 검색 정확도

- 한 논리 줄에 검색어가 여러 번 등장하면 각 occurrence를 별도로 세고
  `F3`/`Shift+F3`로 각각 이동합니다. Search Results에서는 같은 줄을 한 행으로
  묶어 `×N`으로 표시하며, 더블 클릭하면 해당 줄의 첫 occurrence로 이동합니다.
- 앞 공백을 포함한 검색어도 보존하며, C#의 `OrdinalIgnoreCase` 결과를 xterm
  선택 위치와 동일하게 유지합니다.
- 실제 탭, 연속 탭, 후행 공백, CJK/이모지의 wide-character soft wrap을 포함한
  로그도 원본 오프셋에서 렌더링된 xterm 좌표로 변환합니다.
- 커서가 우측 마진에 있어 셀을 만들지 않는 0폭 탭 뒤의 검색 결과도 padding을
  탭으로 오인하지 않고 실제 문자 영역을 선택합니다.

## 대용량 검색 안정성

- 검색을 UI 스레드 밖에서 수행하고 검색어 변경·종료 시 취소를 관찰합니다.
- 20만 줄처럼 큰 retained buffer에서도 전체 occurrence를 세되 Search Results에는
  일치한 로그 줄을 과거부터 최대 1,000개씩 표시합니다. Prev는 과거 방향,
  Next는 최신 방향으로 페이지를 이동하고 최신 페이지의 Next는 검색을 갱신합니다.
- 한 줄에 occurrence가 많은 경우 checkpoint를 사용해 Previous 반복 탐색이
  제곱 시간으로 증가하지 않도록 제한합니다.
- 탭 위치 배열을 모든 결과 줄에 저장하지 않고 매치 주변 누적 개수만 보존해
  TSV 형태 로그의 추가 메모리 사용을 제한합니다.

## 로그 표시 설정

- Settings에서 xterm 화면의 `RX <`와 `TX >` 접두사를 즉시 표시하거나 숨길 수
  있습니다.
- 이 설정은 프로필에 저장되며 검색 payload, 이벤트 탐지, 필터, 파일 로그의
  원본 방향 정보에는 영향을 주지 않습니다.

## xterm 복원 및 Clear 안정성

- 최소화 중 쌓인 로그를 bounded batch로 병합해 복원 시 수천 번의 WebView2
  acknowledgement 왕복으로 지연되는 문제를 수정했습니다.
- Clear가 진행 중인 restore/append보다 우선하도록 generation barrier를 적용해,
  Clear 이전 로그가 뒤늦게 다시 나타나거나 이후 RX가 지워지는 순서 문제를
  수정했습니다.

## 검증

- Release 구성에서 Core 테스트 35개와 WinUI 테스트 232개를 통과했습니다.
- JavaScript 문법 검사와 실제 xterm headless 렌더링에서 일반 탭 및 우측 마진의
  1개·2개 0폭 탭 뒤 `READY` 선택을 확인했습니다.
- 포터블 ZIP과 Inno Setup 설치 파일을 Release 구성으로 생성하고 필수 자산과
  SHA-256 체크섬을 확인했습니다.

## 설치 안내

- 설치 파일과 관리자 권한이 필요 없는 포터블 ZIP을 함께 제공합니다.
- 설치 파일은 코드 서명이 없어 Windows SmartScreen에서 알 수 없는 게시자
  경고가 표시될 수 있습니다.
- xterm 로그 화면을 사용하려면 Microsoft Edge WebView2 Runtime이 필요합니다.
