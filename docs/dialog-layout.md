# 대화상자 버튼과 여백

대화상자는 `CompactControlStyles.xaml`의 `CompactContentDialogStyle`과
`CompactContentDialogTemplate`을 사용한다. 일반 `new ContentDialog`에는 암시적 스타일이
적용된다. XAML/코드에서 파생한 대화상자는 공통 스타일을 명시한다.

- 하단 버튼은 높이 24 DIP, 같은 너비, 간격 8 DIP로 맞춘다.
- 본문과 버튼 영역의 안쪽 여백은 12 DIP, 제목 아래 간격은 8 DIP다.
- 짧은 확인 창은 내용에 맞게 줄어든다. 기본 최소 높이 184 DIP를 사용하지 않는다.
- 기본 버튼/포커스/누름 상태는 크기나 스타일을 교체하지 않는다. 주요 동작만 강조하고 취소는
  보조 버튼을 유지한다. `DefaultButton`의 Enter 처리와 Escape/취소 동작은 WinUI가 맡는다.
- 창마다 높이·버튼 스타일·템플릿을 복제하지 않는다. 최대 폭이나 내용 자체의 스크롤은 창별로 정한다.
  로그 위치 이동 창의 색상은 유지하되 버튼 크기·배치·여백은 같은 틀을 사용한다.

## 점검 범위와 재현

2026-11-15에 앱의 ContentDialog 파생 클래스 10개와 일반 생성 지점 11곳을 조사했다.
값 변경, 전체 복원, 제품 삭제·편집, AI 안내, 항목·그룹·명령·패킷 편집, 시퀀스 단계,
로그 규칙·위치 이동, 일반 확인·편집·오류·이벤트 문맥 창에 공통 틀이 적용된다.

원인은 기본 WinUI 템플릿의 `DefaultButtonStates`가 지정한 24 DIP 스타일을
SDK의 `AccentButtonStyle`로 교체하는 동작이었다. 기본/포커스 버튼이 바뀔 때 높이가 32 DIP로
변했고, 이미 강조된 주요 동작과 취소가 동시에 강조되기도 했다. 공유 템플릿은 네이티브
이름·표시/크기 상태·열기/닫기 애니메이션을 유지하면서 스타일 교체만 제거한다.

기존 실행 파일의 값 변경 창에서 기본 버튼과 포커스를 전환해 크기/스타일 오류 96건을 재현했다.
근거: `artifacts/dialog-footer-before/30c46522d2924d7d848f2952bd05a646/result.json`.

## 검증 방법

`scripts/test_modbus_layout_native.ps1`은 실제 WinUI에서 각 대화상자의 기본 버튼 네 상태와
Normal/PointerOver/Pressed/Disabled 상태를 전환한다. 높이 24 DIP, 균등 너비, 고정 스타일,
12 DIP 하단 여백을 검사한다. 기존 Modbus 전체 내용 검사에 로그/시퀀스 파생 창과
일반 창의 버튼 1·2·3개 구성을 추가했다. 창 크기는 1280×900/800×600이고 일반 창은
Light/Dark 테마를 검사한다. `SERIAL_DIALOG_SCREENSHOTS=1`이면 대화상자 자체를 PNG로 렌더링한다.

이번 변경만 `ebdb0ab` 소스 사본에 적용한 Release 빌드는 경고/오류 0,
관련 WinUI 테스트 730개 통과, 선택적 가상 통신 테스트 3개 제외다.
네이티브 전체 화면 128건과 버튼 상태 실측 2,912건도 통과했다.
값 변경 창은 472×378에서 448×318 DIP로, 전체 복원 예시는 높이 184에서 118 DIP로 줄었다.
기본/취소 누름 상태의 PNG를 확인했고 프로필 변경과 COM 송신은 없었다.
결과: `artifacts/dialog-footer-verified/ea7f8bf22e2e4c9fba26353058df442d/result.json`.
로그: `artifacts/dialog-footer-isolated-build.log`, `artifacts/dialog-footer-verified-tests.log`.
현재 배율의 네이티브 검사이며 실물 통신·장시간 운전 시험은 이 UI 변경에서 수행하지 않았다.

## 키보드와 비동기 적용 확인

`-CaseFilter dialog-keyboard`는 실제 키 입력을 기다리는 선택적 검사다. 기본 자동 검사에서는 실행하지 않는다.
Computer Use로 전용 자식 창에 입력했고 COM 연결·송신은 없었다. 설치 앱과 검사 앱을 혼동하는 입력 도구의
창 식별 오류를 피하기 위해 소스 사본의 AssemblyName만 `DialogKeyboardFixture`로 지정해 빌드했다.

- 전체 복원과 같은 `DefaultButton=Close` 창의 첫 Enter는 `None`(취소)이고 주요 동작은 실행되지 않는다.
- 입력칸의 Enter는 Primary, 보조 버튼에 초점을 둔 Enter는 Secondary를 반환한다.
- Primary가 비활성이면 Enter로 닫히지 않고 Esc는 취소한다.
- Tab 4회와 Shift+Tab 4회는 입력칸→주요 동작→보조 동작→취소→입력칸을 왕복한다.
- 실제 값 변경 창의 비동기 적용 중 중복 Enter·Esc·취소 클릭을 시도했다. 창은 유지됐고 적용 호출은 1회였다.
  실패 문구와 입력 초안이 남았으며 35로 수정 후 Enter로 재시도하면 2번째 호출에서 정상 완료됐다.

첫 기록은 닫힘 애니메이션 동안 복원된 `none` 포커스까지 Tab 이동으로 세어 오탐을 냈다.
네이티브 LayoutRoot가 입력을 차단한 시점부터는 `closingFocusLog`로 분리하고 같은 순환을 재검사해 통과했다.
동작 6건의 원본: `artifacts/dialog-keyboard-final/99278882d7c649ff9be66ac7c894ea02/result.json`.
Tab 재검사: `artifacts/dialog-keyboard-tab/c25e6c3b238b425793f1c645b51eea82/result.json`.

레이아웃 검사의 reflection 콜백도 실제 유효성 오류를 표시하도록 내부 예외를 전달한다.
수정 후 값 편집/오류 화면 4건 통과:
`artifacts/dialog-validation-final/6ada68b1c1784a84ad3f19da5da969f4/result.json`.

동시 진행 작업이 커밋된 `60cef3a` 기준 main에서도 별도 `bin/dialog-footer-final/` 출력으로
Release 빌드 경고/오류 0, 관련 테스트 730개 통과·선택적 가상 통신 3개 제외,
값 변경/오류 화면 4건 통과를 확인했다. 실제 사용 중인 앱은 교체하거나 종료하지 않았다.
로그: `artifacts/dialog-footer-main-build.log`, `artifacts/dialog-footer-main-tests.log`,
`artifacts/dialog-footer-main-native/000b5920c6c94fbda90c7b8dc5242426/result.json`.
