# Manual Regression Test Checklist

- [ ] In Settings, select System, Dark, and Light under Appearance. Verify window,
  title bar, terminal, selection and menus follow the choice without clearing logs
  or restarting serial/file logging. Restart normally and verify the choice is
  restored. With System selected, change the Windows theme; also check Windows
  high contrast remains legible.
- [ ] Run two app instances. Select Light in one and Dark in the other, then
  switch either instance's theme repeatedly. Verify the other instance's terminal,
  selection, scrollbar and context menu keep their own theme. Repeat with System
  in one instance, change the Windows theme, and verify only System follows it.
  Confirm retained logs and ongoing serial/file logging survive each change.
- [ ] Connect two instances to different COM ports (or MOCK). In one, clear,
  filter, search, change font, pause/resume, reconnect, stop a sequence, minimize
  and exit. Verify the other keeps receiving, logging and detecting its own events
  with unchanged terminal history, selection and scroll position. Verify its
  health does not show the first instance's runtime errors.

Use this checklist before release candidates and after UI/layout changes. Prefer
`MOCK` for repeatable checks, then repeat critical connect/TX/logging checks on
real hardware when available.

## 화면 메모리 — 사용자 GUI 확인

Computer Use 없이 사용자가 직접 실행하는 체크리스트다. 시험한 앱 커밋·펌웨어 BUILD ID,
COM·통신 설정, 파일 로그 경로, 시작/종료 시각을 함께 기록한다.
설정과 쓰기 대상의 원래 값을 먼저 남겨 두고 시험 후 복원한다.

준비: Settings → UI / xterm의 **Log MiB**와 **Line scrollback**을 확인하고 파일 기록을 켠다.
보드는 LAB 모드에서 번호가 붙은 STREAM/BURST 데이터를 공급할 수 있다.
구체적인 명령과 기존 GUI 시퀀스는 [펌웨어 안내](../firmware/nucleo-f103rb-serial-lab/README.md)를 따른다.
MOCK로 화면 동작을 먼저 확인한 뒤 실물 UART로 반복한다. MOCK 문자 에코로 Modbus 통신을 판정하지 않는다.

- [ ] **M01 — 설정·저장:** Log MiB를 64 → 256 → 512로 바꾸고 프로필을 저장·불러오기·앱 재시작한다.
  지정한 값이 복원되고, 기존 연결·시퀀스·Modbus 설정도 보존된다. 입력 범위는 64–2048MiB다.
- [ ] **M02 — 오래된 로그만 정리:** 먼저 Line scrollback을 작은 값으로 낮춰 빠르게 한도를 넘긴다.
  앞부분부터 없어지고 최신 번호가 계속 추가된다. 화면 전체가 비거나 이미 본 로그가 중복되지 않는다.
  이어서 줄 수 한도를 충분히 높이고 64MiB 예산을 실제로 넘길 만큼 수신해 같은 동작을 확인한다.
  짧은 BURST 1,000줄 성공만으로 메모리 예산 도달 시험을 통과 처리하지 않는다.
- [ ] **M03 — 실행 중 예산 변경:** 충분한 이력이 있을 때 예산을 낮추면 오래된 화면 이력이 즉시 정리된다.
  높이면 이후 수신분을 더 보관하고 이미 제거된 로그는 돌아오지 않는다. 수신·파일 기록은 계속된다.
- [ ] **M04 — 과거 탐색·최신 복귀:** 수신 중 과거로 스크롤하고 선택·복사한 뒤 최신 위치로 돌아온다.
  보관 중인 내용이 일치하고 복귀 후 새 로그가 계속 보인다. 한도 때문에 제거된 범위는 다시 나타나지 않는다.
- [ ] **M05 — Pause View:** `Keep saving file log`를 켜고 Pause View가 `Resume Live`로 바뀔 때까지 기다린다.
  화면은 멈추고 RX·파일 기록은 계속된다. Resume 후 정지 기간 로그를 재생하지 않고 경계 표시와 새 로그가 나온다.
  파일 기록 옵션을 끈 경우에는 정지 기간 파일 기록 생략과 gap 표시가 기대 결과다.
- [ ] **M06 — 최소화·복원·Clear:** 수신 중 최소화·복원을 반복하고 복원 중 Clear도 실행한다.
  이전 로그가 중복되거나 Clear 전 로그가 되살아나지 않고, 이후 수신은 정상적으로 이어진다.
- [ ] **M07 — 줄바꿈·문자·창 크기:** 긴 줄, 개행 없는 데이터, 한글·이모지·탭·ANSI 색상 데이터를 받은 뒤
  창을 좁혔다 넓힌다. 남은 글자·색상·줄 연결·선택 복사가 일치하고 커서 위치가 깨지지 않는다.
- [ ] **M08 — 검색·필터·이동:** 정리 전후에 알려진 문자열을 검색·강조·필터링하고 결과/마커 위치로 이동한다.
  보관 중인 로그로 이동하며 다른 줄을 가리키거나 제거된 로그를 되살리지 않는다.
- [ ] **M09 — 화면과 파일 분리:** 화면에서 사라진 초기 번호가 파일에는 남아 있는지 확인한다.
  파일 회전이 있다면 모든 파일을 포함해 확인한다. 전체 누락·중복 판정은 원본 바이트/순번 검증기로 수행한다.
- [ ] **M10 — 장시간:** 동일한 입력·보관 설정으로 시작/한도 도달/이후 일정 간격의 메모리·CPU·오류를 기록한다.
  한도에 도달한 뒤에도 메모리가 계속 누적 상승하거나 조작이 멎지 않는지 확인한다.
  앱과 WebView2 메모리를 함께 관찰한다. Log MiB는 보관 데이터 예산이므로 프로세스 전체 RAM 상한과 같지 않다.
  30–60분 예비 시험과 요구사항인 72시간 내구 결과는 구분한다.

세부 동작·측정 범위: [로그 보관 검증](history-retention-validation.md).

## Modbus — 사용자 GUI 확인

기존 **Protocol → Modbus RTU → 레지스터 모니터**를 사용한다.
NUCLEO LAB의 기존 RTU 기본값은 **COM3 / 115200 / 8N1 / 흐름 제어 없음 / 장치 ID 1**이다.
연결 후 실제 펌웨어 버전과 [레지스터 맵](../firmware/nucleo-f103rb-serial-lab/MODBUS-RTU.md)을 확인한다.
기존 1.6.0은 FC03/04 주소 0·1의 250·100을 읽는 시험부터 가능하다.
FC01/02·쓰기·카운터·LED·추가 오류 주입은 해당 기능을 지원하는 확장 펌웨어와 그 맵을 사용한다.
쓰기 시험은 시험 보드의 지정된 영역에서 수행하고 초기 값을 복원한다.

- [ ] **B01 — 한 번 읽기:** ID 1, FC03, 시작 주소 0, 수량 2로 한 번 읽는다.
  해당 빌드의 기대값·원본 HEX·응답 시각·성공 횟수가 맞는다. 주소는 40001 표기가 아닌 **0 기준 오프셋**이다.
- [ ] **B02 — 폴링·보기 전환:** 주기 500ms, timeout 1,000ms 등으로 폴링한다.
  값·시각·통계가 갱신되고 패킷 분석/레지스터 모니터 전환 후에도 계속된다. 양쪽 보기에서 정지할 수 있다.
  주기는 응답 처리 뒤의 대기 시간이므로 정확한 500ms 고정 주기로 판정하지 않는다.
- [ ] **B03 — FC01/02/03/04:** 지원되는 각 영역을 읽어 비트/레지스터 값과 주소를 비교한다.
  FC02/04에는 쓰기를 허용하지 않는다. 지원되지 않는 영역은 미실행으로 기록한다.
- [ ] **B04 — 해석·배율:** 고정 시험 패턴으로 UInt16/Int16/UInt32/Int32/Float32, 배율, 바이트/워드 교환을 확인한다.
  원본 HEX는 그대로이며 해석 값만 기대대로 바뀐다. 32비트 값은 두 레지스터를 묶고 홀수 마지막 값은 부족 상태를 보인다.
- [ ] **B05 — 카운터·LED:** 확장 펌웨어의 카운터가 변하고 변경 표시·응답 시각이 갱신된다.
  지정된 LED 코일을 1/0으로 쓰면 실제 LED와 재조회 값이 함께 켜짐/꺼짐을 나타낸다.
- [ ] **B06 — 직접 쓰기·재조회:** FC05/06 단일 쓰기와 FC0F/10 복수 쓰기를 각각 시험한다.
  응답 확인 후 다시 읽은 원시 값이 입력과 일치해야 완료다. 쓰기 입력은 배율을 적용하지 않은 원시 값이다.
- [ ] **B07 — 쓰기 중 선택·정지:** 폴링 중 한 건을 쓰고 다른 행을 선택하거나 추가 쓰기·정지를 누른다.
  진행 중인 쓰기의 대상은 바뀌지 않고 추가 대기는 거절된다. 완료 후 새 선택의 주소와 값이 함께 반영된다.
  이미 송신한 쓰기를 정지했을 때 재조회가 끝나지 않았다면 미확인으로 표시하며 자동 재시도하지 않는다.
- [ ] **B08 — 입력 경계:** ID 0, 범위 밖 주소/수량, 주소+수량 초과, 잘못된 쓰기 값·감시 영역 밖 쓰기를 입력한다.
  오류를 알리고 잘못된 요청을 송신하지 않는다. 읽기 상한은 2,000비트/125레지스터다.
- [ ] **B09 — 송신 충돌:** 폴링 중 수동 TX·터미널 직접 입력·시퀀스 실행·통신 설정 변경을 시도한다.
  차단되며 기존 Modbus 통신을 망가뜨리지 않는다. 시퀀스 실행 중 Modbus 시작도 차단된다.
- [ ] **B10 — 예외·재조회 불일치:** 유효한 Modbus 예외 응답 또는 쓰기 재조회 불일치를 유도한다.
  성공으로 표시하지 않고 실행을 끝낸다. 이 경우 물리 연결은 유지되며 자동으로 다시 쓰지 않는다.
- [ ] **B11 — timeout·CRC·불완전 응답:** 지원되는 오류 주입을 한 종류씩 실행한다.
  레지스터 모니터는 오류를 표시하고 원래 연결을 종료한다. 자동 재연결/폴링 재시작은 하지 않는다.
  사용자가 다시 연결하고 대상을 확인한 뒤 한 번 읽기로 복구를 확인한다.
- [ ] **B12 — 연결 교체·이전 값:** 수신/쓰기 중 연결을 끊었다 다시 연결한다.
  이전 값은 현재 정상 값으로 남지 않고 stale/오래된 상태가 된다. 이전 대기 명령이 새 연결로 전송되지 않는다.
  대상 ID/주소를 편집했을 때 이전 값·통계가 새 대상의 결과로 표시되지 않는다.
- [ ] **B13 — 저장·재시작:** 메모리 예산과 Modbus 대상/표시 설정을 함께 저장한 뒤 다시 실행한다.
  설정은 복원되지만 폴링·쓰기가 자동 시작되지 않는다. 잘못된 Modbus 초안을 남기고 일반 설정을 저장해도
  다른 설정이 없어지지 않는다. Modbus 전용 저장/실행은 잘못된 입력을 거부한다.

세부 제한·정지·오류 정책: [Modbus 레지스터 모니터 안내](modbus-rtu-monitor.md).

## 한 번에 자동 시험하는 경로

**현재 바로 사용할 수 있는 GUI 시퀀스:** Modbus 폴링과 다른 송신 작업을 먼저 정지한 뒤,
Sequences → 시퀀스 관리 → JSON 불러오기에서
[정상·예외·CRC·복구 시퀀스](../firmware/nucleo-f103rb-serial-lab/sequences/modbus/02-rtu-server.gui.json)를 추가한다.
RTU 모드의 보드에 한 번 실행하면 읽기·주소 예외·CRC 손상·복구·FC04·수량 예외를 순서대로 검사한다.
기존 맵과 같은 응답을 내는 펌웨어에서 RX Check 6개 일치가 기대값이다.
CRC 손상 바이트를 정확히 받으면 그 RX Check는 일치이고 패킷 분석에는 CRC 오류로 표시된다.

이 시퀀스는 HEX 패킷과 RX 패턴을 시험한다. **레지스터 모니터의 폴링/오류 종료 동작은 B01–B13으로 따로 확인한다.**
GUI RX Check는 미일치해도 다음 단계로 진행하며 동적 CRC 계산·수치 비교·분기·자동 복구는 지원하지 않는다.
변하는 카운터의 정확성이나 전체 파일 무결성을 패턴 하나의 일치만으로 통과 처리하지 않는다.

**기존 자동 UART 검증기:** GUI에서 해당 COM 연결을 닫은 뒤 저장소 루트에서 다음을 실행한다.
보드에 기록된 이미지와 일치하는 `manifest.json`이 `build-rtu-server`에 있어야 한다.
다른 빌드 디렉터리라면 `-BuildDirectory`에 그 이름을 지정한다. 이 명령은 빌드·보드 기록을 수행하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\firmware\nucleo-f103rb-serial-lab\scripts\verify-rtu-server.ps1 -Mode Live -Port COM3 -BuildDirectory build-rtu-server
```

기존 검증기는 정상/오류/무응답/복구 17개 사례의 원본 UART·CRC·앱 분석기 결과를 판정하고 `results/`에 증거를 남긴다.
화면 렌더링이나 레지스터 모니터 GUI 조작까지 자동 검증한 결과는 아니다.
확장 펌웨어의 쓰기·카운터·LED·오류 주입 자동화는 그 빌드와 함께 제공되는 최신 실행 안내를 따른다.
실행 결과에는 기대한 오류와 예상하지 못한 실패, 미실행 항목을 구분한다.

확장 시험을 한 실행에 묶을 때의 순서는 **빌드 ID 확인 → 정상 읽기 → 단/복수 쓰기·재조회·적용 횟수 확인 →
카운터 변화 → LED 코일 → 오류 주입·정상 복구 → 원래 값 복원 → 보고서 저장**으로 한다.
LAB 부하 생성은 RTU 시험을 끝낸 다음 별도 구간에서 실행한다. 번호 로그·파일 무결성과 메모리 추이를 기록하고,
화면 설정·선택·최소화·복원 결과는 위 수동 체크리스트에 남긴다.

## Startup

- [ ] Run a plain TX sequence with no enabled RX Check, including one with
  only disabled RX Check steps. Confirm no sequence-run folder/RX capture is
  created and result storage adds no wait before/between TX. Existing normal
  file logging must still follow its own Log ON/OFF setting.
- [ ] In the sequence step editor, add separate TX and RX Check steps; in HEX use `AA 55 ?? 00`
  with 1000ms. The time input must remain editable, and an early match must still wait the full 1000ms. Split RX into AA / 55 37 / 00 and merge it with unrelated
  bytes: verify matching across chunks. Invalid nibble wildcards/all-wildcard
  patterns must fail before TX. A text pattern must remain literal/case-sensitive.
- [ ] Run mixed checked/unchecked/disabled steps for several repeats. A timeout
  must continue in order without retrying. Pause waits for response observation
  plus the normal delay; Stop cancels immediately without erasing an already
  completed match. Disconnect must not match bytes from a new connection generation.
- [ ] Verify an immediate RX during the preceding TX delay can satisfy the next RX Check.
  The RX step must never send its pattern or apply a second delay; earlier repeats and post-reconnect RX must not supply a match for the preceding TX.
- [ ] Receive a complete response before disconnect/reconnect, both during the TX delay and during RX Check.
  Verify it remains matched with the original RX time and highlight. Repeat with an incomplete old response:
  bytes received after disconnect (including a complete reply on the new connection) must not complete the match.
- [ ] Confirm each TX/RX line shows its original HH:mm:ss.fff time, including split UTF-8/CP949 characters and RX paging.
- [ ] Open execution results while running: confirm the summary, repeat/status
  filters, 200-row paging, RX paging and match highlighting. Close/reopen the
  window and restart the app: stored results remain. Normal Log OFF must not
  discard sequence results. Closing the main window must close its results window.
- [ ] Verify legacy v1 files retain Expect/CaptureMs and their existing CLI/MCP
  semantics. Export with RX Check uses v3 (v2 attached checks migrate to explicit steps) and preserves it across copy,
  reorder, profile merge and restart. Legacy CLI/MCP must explicitly reject live
  conditions and attempts to overwrite them, with no device TX/profile mutation.
- [ ] While a GUI sequence is starting, checking, paused, reconnecting or flushing
  results, CLI/MCP mutations and bridge startup must return busy; manual TX must
  be blocked. While CLI/MCP owns capture, GUI sequence start must be disabled.

- [ ] In Terminal mode, split UTF-8 Korean bytes after the first/second byte of
  a character at a partial flush boundary (128 bytes or 250 ms idle). Repeat
  with CP949 split after the leading byte. Verify the combined text is intact,
  raw RX is unchanged, and an incomplete character alone does not increase the
  decode-error count. A truly incomplete final character must still be reported
  when the line/session ends; a new session must not inherit its pending bytes.
- [ ] Start two windows using the shared profile. Register a sequence in A,
  then save settings and exit B without reloading its stale sequence list.
  Repeat the stale save, restart and verify A's sequence remains. Delete a
  sequence in one window and save an unchanged older copy in the other; verify
  it is not resurrected. Concurrent different edits to the same sequence must
  report a conflict and preserve the last committed file.

- [ ] With the GUI running, use `serialctl sequence save --file check.json` to
  register a documented device sequence. Verify it appears in the GUI, causes
  no TX, and survives app restart. Duplicate names require `--overwrite true`.
  A failed profile save must leave the GUI sequence collection unchanged.
- [ ] Start via CLI or `serial_sequence_start`, query the returned runId and
  verify ordered, one-time TX plus progress in the GUI. Close the initiating
  CLI after start; the accepted run must continue. Try GUI Stop, AI stop and
  `sequence cancel --run ID`; control returns after RX has been saved. Manual
  TX, sequence editing, profile replacement and Bridge must be blocked while
  running. Disconnect during a step; no further steps or automatic TX retries.
- [ ] In Sequences, create three MOCK commands with a visible delay and disable
  the middle step. Run twice: only the first and third commands/delays execute,
  progress counts four active steps and the displayed editor step numbers stay
  1 and 3. Disable all: Run is unavailable. Re-enable and export/reload/save/restart:
  checkbox state and all original steps remain. A legacy JSON without IsEnabled
  opens with all steps enabled. CLI/MCP also omits disabled commands and delays.
- [ ] Copy a step and a sequence, then edit the copy. Verify the original keeps
  its command, mode, enabled state and metadata. The list shows only command
  text on one line, including when a step has a name or multiline payload.
  In the step dialog, change Global/Terminal/Hex; invalid explicit HEX cannot be saved.
- [ ] Use the single sequence play/pause button: Play starts, the icon changes
  to Pause, and clicking it waits for the current send/delay before pausing.
  The icon changes back to Play; clicking it continues the same run. Cancel a
  pending pause by clicking Play again; Stop cancels while paused. Pause during automatic
  reconnect must still hold after reconnect. Successful TX must not replay.
  Check editing is locked during the run and CLI/MCP exposes Stop only.
- [ ] Compare optional expected `abcd` with actual `abcd` and `ab12c3d4`.
  Verify `matched` and `not_matched` respectively, without treating a miss as
  product failure or retrying. Follow captureId/nextOffset to inspect complete
  RX, including data after an early match and between steps. RX gaps/errors
  must make match evidence inconclusive. Repeat with literal HEX bytes.

- [ ] Enable Cute background mode, choose a JPG/PNG (also try a path with spaces
  and Korean text), and enter opacity values 0, 1, 37, 73 and 100 percent.
  Confirm 0 hides the image and 100 shows it fully, with intermediate values
  changing its visibility. The main xterm surface remains opaque.
- [ ] Confirm the opacity editor matches other Settings inputs (200 x 24), has
  a separate `%` suffix and no spin buttons. While focused/hovered, scroll the
  mouse wheel and press Up/Down; the value must stay unchanged. Enter/paste
  `101`, `1000` and a very long integer: each must become `100`. Try letters,
  `12.5`, `-1`, `25%` and mixed pasted text: reject the entire edit and keep the
  previous value. Clear the field, then press Enter or leave it: restore the last
  valid value. Select all and replace with `0`, `37`, `100`; verify immediate
  application and correct restoration after restarting the app.
- [ ] Close normally without pressing Save Profile, restart, and confirm the
  background mode, image path and opacity are restored. Repeat while receiving
  MOCK logs and with background mode disabled. Temporarily rename the image,
  restart, and confirm the default image appears while the selected path stays
  saved. Restore the file and restart to confirm the custom image returns.
- [ ] In Settings, verify every serial selector, appearance selector and UI
  selector uses the same width and aligned label/control columns. Check the
  longest values (OnePointFive, DtrRtsXOn, JetBrains Mono (Bundled), and
  yyyy-MM-dd HH:mm:ss.fff) fit fully including the dropdown arrow. Repeat for
  Bridge and Test selectors and the HEX timeout input.
- [ ] Narrow the inspector/window and verify setting fields keep their width
  and remain reachable by horizontal scrolling. Labels, paths, explanations
  and status text should wrap instead of using an ellipsis or clipping a line.

- [ ] Start the app.
- [ ] Confirm the bridge is `BRIDGE OFF` after startup, including when the saved
  profile previously contained an enabled bridge and the device COM auto-connects.
- [ ] Disconnect/reconnect the device and confirm the bridge stays OFF until
  `Start bridge` is pressed explicitly.
- [ ] Confirm the window opens without an unhandled exception.
- [ ] Confirm the Port list includes one `MOCK` entry.
- [ ] Confirm bottom health shows `HEALTH OK` or a clear non-stale warning.
- [ ] Hover over the footer and confirm the health reason and counter legend appear.
- [ ] Right-click the footer, copy status, and paste it into Notepad.

## Window Names

- [ ] Select a port and click the name button at the far right of the top row.
  Verify the input receives focus and Enter applies the name to the button and
  native title (`Main board · COM3 - Serial Monitor`).
- [ ] Edit the name, press Esc or click outside, and verify the previous name remains.
- [ ] Apply an empty name and verify the title falls back to the COM port only.
- [ ] Rename while MOCK RX and file logging are active; verify both continue and
  the log filename and command history remain unchanged.
- [ ] Narrow the window and verify the button remains visible; long names must
  show an ellipsis with the full name available in the tooltip.
- [ ] Switch ports and restart; verify names restore for their respective ports.
- [ ] Run two instances on different ports and save both names, then exit/restart.
  Verify neither name was lost or copied to the other port.

## MOCK Connect And Disconnect

- [ ] Right-click the top Logging button and open the log file location. While
  logging, verify it opens the active file's folder without toggling logging.
  After stopping, verify it opens the last log's folder; before any log exists,
  verify it opens the configured Save dir. Repeat with spaces and Korean text
  in the folder path, and after changing Save dir while a previous log exists.
- [ ] Pause the log display and verify the play icon uses the existing error-red
  color. Resume and verify the pause icon retains its original foreground.
  Repeat in Light and Dark and switch theme while paused; high contrast must
  retain the app's accessible error color.

- [ ] Select `MOCK`.
- [ ] Click Connect.
- [ ] Confirm connection state changes to connected.
- [ ] Confirm RX logs start appending.
- [ ] Confirm the footer changes to `File ON` while file logging is active.
- [ ] Disconnect.
- [ ] Confirm FileWriter stops cleanly.
- [ ] Reconnect to `MOCK`.
- [ ] Confirm no duplicate mock streams, duplicate events, or duplicate log rows.

## Real COM Connect Placeholder

- [ ] Connect a known serial device.
- [ ] Select its COM port and baud rate.
- [ ] Connect and confirm RX logs arrive.
- [ ] While connected, switch Terminal/HEX mode repeatedly and confirm the same
  COM port remains selected and connected while new data uses the active mode.
- [ ] With LOG ON, switch Terminal/HEX in both directions: visible history and
  search results clear, the same log file retains pre-switch records and receives
  post-switch records, and pending bridge/xterm batches do not restore old history.
  Repeat while the view is paused, then resume. Selecting the current mode or
  changing only the HEX timeout must not clear history.
- [ ] In Terminal mode, drag across two timestamped log lines and confirm the
  tooltip shows only `Δt: <duration>`; selecting within one logical line shows
  `Δt: 0ms`.
- [ ] Keep dragging in Terminal mode while new MOCK lines append or the terminal
  auto-scrolls; confirm the delta tooltip remains visible until mouse release.
- [ ] Hide visible timestamps and repeat the Terminal selection; confirm the
  delta tooltip still works and copied text contains no tooltip metadata.
- [ ] In HEX mode, drag across known bytes on two log lines and confirm the
  tooltip uses two lines: `Len: <bytes>` followed by `Δt: <duration>`. Confirm
  the byte count matches the previous selection-length behavior.
- [ ] In HEX mode, change the HEX group timeout and confirm it applies without
  disconnecting the COM port.
- [ ] Confirm `RX BUSY -- · IDLE -- · PEAK -- · 1m` appears to the right of the HEX timeout
  while disconnected in HEX mode.
- [ ] Connect in HEX mode and confirm the bus meter changes to a warm-up duration,
  then shows `1m` after 60 seconds.
- [ ] During warm-up, confirm BUSY and IDLE percentages are available after the
  first sample, PEAK remains `--`, and duration is shown as `<seconds>/1m`.
- [ ] Reset immediately before a timer tick, inject a short high-rate burst, and
  confirm it does not become a false full-second PEAK when the first minute
  completes.
- [ ] Generate one high-rate RX burst followed by a lower steady rate and confirm
  PEAK holds the busiest approximately one-second interval, then rolls out 60
  seconds after that interval.
- [ ] Press Clear in HEX mode and confirm BUSY/IDLE/PEAK return to the collecting
  state and start a new warm-up window. Confirm file logging continues normally.
- [ ] Confirm the bus meter disappears in Terminal mode and restarts its warm-up
  window when switching back to HEX mode.
- [ ] With known continuous RX traffic, confirm BUSY includes framing time and
  one trailing character of idle qualification, without adding a guard per
  driver chunk in a contiguous stream.
- [ ] With individually timestamped bytes and gaps shorter than one character,
  confirm those gaps stay BUSY; with longer gaps, only silence after the first
  character of quiet time becomes IDLE. Confirm the tooltip shows the applied
  character duration (about 86.806 µs at 115200/8N1).
- [ ] Change the HEX group timeout and confirm it does not change the idle
  qualification duration. Confirm native timeout silence is not counted as RX.
- [ ] Send local TX traffic without RX echo and confirm it does not increase the
  RX BUSY estimate.
- [ ] Send a harmless command.
- [ ] Disconnect and reconnect.
- [ ] Confirm no app crash or stale connection state.
- [ ] Enable automatic reconnect, unplug the adapter during RX, and confirm the
  top button changes to `Cancel Reconnect` while retries use 1, 2, 5, then 10
  second delays.
- [ ] Reinsert the adapter with the same COM number and confirm RX resumes once,
  without duplicate rows or duplicate observers.
- [ ] With LOG ON, repeat the unplug/reinsert test and confirm logging continues
  in the same active file rather than creating or appending another named file.
- [ ] Start a bridge and a command sequence separately, force an unexpected
  disconnect, and confirm each is stopped and not automatically resumed.
- [ ] Run a sequence whose current step has a 600-second delay, unplug the
  adapter, and confirm the sequence leaves Running immediately without waiting
  for the COM port to return.
- [ ] During retry, click `Cancel Reconnect` and confirm retries stop, background
  connection resources are cleaned up, and a later manual Connect succeeds.
- [ ] Let one reconnect open attempt fail, then close the app while it is waiting
  for the next retry. Confirm the process exits and no later COM open occurs.
- [ ] Disable automatic reconnect, force another fault, and confirm no retry is
  scheduled. Confirm a manual Disconnect never schedules a retry.

## Bidirectional COM Bridge

- [ ] Confirm the configured com0com pair `COM4 <-> COM5` is present.
- [ ] Connect Serial Monitor to the real device COM port.
- [ ] Open the Bridge tab and select `COM4` as the app-side virtual port.
- [ ] Start the bridge and confirm `BRIDGE ON` appears in both the top and bottom status areas.
- [ ] While the bridge is running, click Refresh and confirm the selected virtual port and route remain unchanged.
- [ ] Open an external controller or analyzer on `COM5`, not `COM4`.
- [ ] Confirm real-device RX bytes arrive unchanged at the external program.
- [ ] Send binary/HEX data from the external program and confirm the exact bytes reach the device.
- [ ] Confirm virtual-to-device traffic appears as RX in the app log without a
  `[BRIDGE]` prefix.
- [ ] In Terminal mode with UTF-8 selected, send bytes `45 52 52 4F 52` and
  confirm the log shows `ERROR`, a Terminal `RxOnly` rule fires, and an
  equivalent HEX rule does not fire.
- [ ] In HEX mode, send bytes `45 52 52 4F 52` and confirm the log shows
  `45 52 52 4F 52`, a HEX `RxOnly` rule fires, and an equivalent
  Terminal rule does not fire.
- [ ] In Terminal mode, split one UTF-8 multibyte character across consecutive
  bridge writes and confirm it is decoded without replacement characters.
- [ ] Repeat with CP949 and confirm a Terminal keyword spanning consecutive
  bridge chunks still matches.
- [ ] Change RX encoding and Terminal/HEX display modes; confirm forwarded bytes remain unchanged.
- [ ] Pause rendering and minimize the app while sending sustained virtual-to-device traffic; confirm the device continues receiving without bridge transport drops.
- [ ] If the UI-only bridge log queue is forced to overflow, confirm only its UI drop counter increases while transport byte counts continue.
- [ ] While Bridge is OFF, confirm the normal awaited RX pipeline remains active and footer drop counts stay at zero.
- [ ] While Bridge is ON, force parser/UI overload and confirm raw bridge traffic continues while only the bridge-priority parser/log drop counter increases.
- [ ] Stop the external program temporarily and confirm bridge backlog/drop/error counters remain bounded and visible.
- [ ] While bridge traffic is active, request one manual TX and confirm the UI
  shows `TX waiting for bridge idle`; verify a second button/Enter/saved/history/
  shortcut request is rejected as Busy and the first payload is sent once after
  25 ms of global bridge idle.
- [ ] Confirm Send, Enter, Quick command, saved shortcut, and history resend are
  disabled immediately on the Waiting transition and remain disabled through
  Sending, without waiting for the footer status refresh timer.
- [ ] Split one HEX rule pattern at every byte boundary across consecutive
  writes on COM5 and confirm the TxOnly/Both rule fires once for each logical
  idle group while the bytes reaching the device remain unchanged.
- [ ] Send HEX continuously without a 25 ms idle gap and confirm display records
  appear within 50 ms or 256 bytes, each `RawBytes` payload is at most 256 bytes,
  and a pattern split across two display records does not match Event, Highlight,
  or View filter rules.
- [ ] Stop the bridge while a COM5 read is completing and confirm normal stop
  does not increase virtual-to-device drop/overflow counters or set a fault.
- [ ] Confirm command sequences cannot start while Bridge is ON.
- [ ] Saturate the device-to-virtual queue and confirm the bridge alone stops
  with `Bridge stopped: virtual COM consumer too slow`, while the device COM and
  normal RX remain connected.
- [ ] Force a bridge warning or failure and confirm the footer health tooltip
  keeps the reason visible and copied status includes it.
- [ ] Stop the bridge and confirm the `BRIDGE ON` indicators disappear.
- [ ] Disconnect the device and confirm the bridge stops before the device port closes.

## Xterm Log View

- [ ] Confirm xterm log lines append smoothly.
- [ ] Resize the app and confirm the xterm area fits the available space.
- [ ] In Settings > Font, change the xterm font family and size; confirm both
  apply immediately without clearing or duplicating visible log lines.
- [ ] Save the profile, restart or reload it, and confirm the selected xterm font
  family and size are restored.
- [ ] Drag-select multiple xterm lines.
- [ ] Press Ctrl+C.
- [ ] Paste into Notepad and confirm selected text copied.
- [ ] Under sustained traffic, click Pause View and confirm it briefly shows
  `Pausing View`, then `Resume Live`. Once `Resume Live` appears, confirm the
  terminal no longer moves.
- [ ] With `Keep saving file log` checked, confirm RX/file/event counters continue
  increasing, UI pending stays bounded, PS increases, and Drop UI remains unchanged.
- [ ] Leave the view paused for more than 10,000 incoming records and confirm the
  serial log file continues growing beyond that point while Q U stays bounded.
- [ ] Click Resume Live and confirm paused records are not replayed, a resume
  boundary containing the pause PS count is shown in gray, and only newly
  received records append afterward.
- [ ] While paused, change a display format/filter if practical, minimize and
  restore, then resume; confirm pause-period records never reappear after a full
  re-render or restore.
- [ ] Clear `Keep saving file log`, repeat the pause, and confirm RX/events continue
  while pause-period records are omitted from the file and the gap is marked.
- [ ] Click Clear and confirm only the visible terminal clears.
- [ ] Minimize under sustained traffic, restore with at least 5,000 one-line
  records pending, and click Clear while the restore overlay is visible. Confirm
  the overlay closes promptly, no pre-Clear lines reappear, and RX lines received
  after the click appear after the cleared boundary without requiring a full
  re-render.
- [ ] Repeat while deliberately delaying restore cancellation if using a debug
  hook, then minimize again before Clear completes. Confirm the latest Clear owns
  the barrier, no pre-Clear live batch reappears, and live appends resume exactly
  once after the window is restored.

## TX Manual Command

- [ ] Connect to `MOCK` or hardware.
- [ ] Enter a command in TX.
- [ ] Choose TX ending.
- [ ] Click Send.
- [ ] Confirm a TX line appears in xterm.
- [ ] Confirm the TX line appears in the serial log file.
- [ ] Confirm Send is disabled or fails gracefully while disconnected.

## Saved Commands

- [ ] Confirm the toolbar reads `Quick CMD  +  |  ps  meminfo …`, with no Saved dropdown or standalone Edit/Del buttons.
- [ ] Click `+` and add a saved command named `reboot` with command text `reboot`.
- [ ] Confirm a compact `reboot` quick-send button appears.
- [ ] Connect and click `reboot`.
- [ ] Confirm TX logging works.
- [ ] Right-click `reboot`, choose Edit, and confirm the chip updates after saving.
- [ ] Send one command, then right-click a different command and confirm Edit/Delete targets the right-clicked command.
- [ ] Right-click `reboot`, choose Delete, and confirm it disappears after confirmation; Cancel leaves it intact.
- [ ] Confirm add/edit/delete works while disconnected.
- [ ] With enough commands to overflow, confirm only the command list scrolls and `Quick CMD` / `+` stay visible.

## Saved Command Shortcut

- [ ] Add or edit a saved command with shortcut `Ctrl+1`.
- [ ] Focus the main window or xterm and press Ctrl+1.
- [ ] Confirm the command sends through the normal TX path.
- [ ] Focus a text input and press Ctrl+1.
- [ ] Confirm the shortcut does not interfere with typing.

## Command Sequences

- [ ] Open the `Sequences` tab.
- [ ] Add a sequence named `Boot Check`.
- [ ] Add steps:
  - `version`, delay 300 ms
  - `status`, delay 300 ms
  - `help`, delay 300 ms
- [ ] Confirm the selected sequence is chosen through the compact selector.
- [ ] Confirm steps are visible and scrollable in small-height windows.
- [ ] Edit a step and confirm the row updates.
- [ ] Move a step up and down.
- [ ] Delete a step.
- [ ] Connect to `MOCK`.
- [ ] Run the sequence.
- [ ] Confirm TX lines appear in order.
- [ ] Switch to HEX mode, add a sequence step such as `AA 55`, run it, and
  confirm the device receives bytes `0xAA 0x55` rather than ASCII text.
- [ ] Stop during execution and confirm it stops safely.
- [ ] Save Profile, restart, and confirm the sequence is restored.

## Markers

- [ ] Click Quick Mark.
- [ ] Confirm `MARK > User marker` appears in xterm.
- [ ] Open Custom Mark and enter `test start`.
- [ ] Confirm `MARK > User marker: test start` appears.
- [ ] Press Ctrl+M and confirm a default marker appears.
- [ ] Confirm markers are not sent to the serial port.
- [ ] Confirm markers are searchable.
- [ ] Confirm markers appear in the serial log file.

## Log File Name And Runs

- [ ] Start the app and confirm the header shows red-orange `LOG OFF` even if the
  previously saved profile had logging enabled.
- [ ] Connect and confirm no serial log is written until `LOG ON` is pressed.
- [ ] Enter log file name `inverter test.log` and confirm the adjacent `Current:` value shows
  exactly `inverter test.log`.
- [ ] Press `LOG ON` and confirm only `inverter test.log` is created, with no
  date, time, `_serial`, or extra extension added.
- [ ] Confirm the file-name field is disabled while LOG is ON and enabled again
  after `LOG OFF`.
- [ ] With `inverter test.log` still present, press `LOG ON` and confirm it is
  refused rather than appended to or overwritten.
- [ ] Clear the file name, press `LOG ON`, and confirm a new automatic
  `yyyy-MM-dd_HHmmss_serial.log` file is created.
- [ ] Confirm `LOG ON` is light green and `LOG OFF` is red-orange in both the
  header and Settings.

## Event Detection And Original Logs

- [ ] Connect to `MOCK`.
- [ ] Wait for WARN/ERROR/FAULT events.
- [ ] Open Events tab and confirm events append incrementally.
- [ ] Select an event.
- [ ] Confirm the Context tab is absent; Events and rule notification controls remain.
- [ ] Double-click a retained event and confirm its original log is selected in
  the main terminal, with surrounding lines available for reading.
- [ ] Continue receiving logs and confirm the jumped position stays visible until
  Auto Scroll is explicitly enabled again.
- [ ] Double-click a filtered or expired event and confirm a clear status message
  appears without jumping to a different log with the same message.
- [ ] Confirm event detection and original-log navigation work with Log Save OFF.
- [ ] Confirm no `*_events.log` file is created.

## Event Auto And Latest

- [ ] Open Events tab.
- [ ] Confirm Auto is enabled by default if expected.
- [ ] Confirm new events keep the newest row visible.
- [ ] Turn Auto off and select an older event.
- [ ] Confirm selection is stable as new events arrive.
- [ ] Click Latest.
- [ ] Confirm the newest event is selected and scrolled into view.

## Event Notifications

- [ ] In both Terminal and HEX, turn Event OFF, leave RX trigger sequence at
  `(none)`, and enable each of Tray, Sound, and Popup separately. Matching data
  should notify without adding an Events row or starting a sequence.
- [ ] Confirm Tray, Sound, and Popup are all OFF for existing/default rules.
- [ ] Enable Popup for one MOCK event rule and keep cooldown at 30 seconds.
- [ ] Confirm a matching MOCK event shows one non-blocking popup after about 1 second.
- [ ] Confirm the popup closes automatically after about 8 seconds.
- [ ] Generate repeated matches and confirm they are grouped into at most one notification per 30 seconds.
- [ ] Enable Sound and confirm one sound plays per grouped notification, not once per event.
- [ ] Enable Tray and confirm a Windows tray balloon appears.
- [ ] Disable all three options and confirm events continue to populate without any notification.

## Highlight Rules

- [ ] Delete every rule, save the profile, and restart. Rules must stay empty.
- [ ] Choose Orange as a rule's background, open Edit, save, and reload the
  profile. Orange must remain selected in both Terminal and HEX rules.
- [ ] In Terminal, send `FA` and then `ULT` without a newline, with Event,
  Highlight, and Filter configured for `FAULT`. Detection and the filtered
  log must include `FAULT`, and the completing fragment must be highlighted.
  Repeat with the newline included in the final write, and after an async
  filter change. The final fragment must stay on the existing logical line.
- [ ] Repeat the split test with an equivalent HEX rule. Separate records must
  not match; a complete pattern in a later record must match and highlight.
- [ ] Let the filtered match's hidden prefix expire from the retained buffer.
  The view must agree with Refresh/ALL, without stale prefixes or broken timestamps.
- [ ] Load legacy event and highlight rules sharing a name/keyword but using
  different RX/TX directions. Preserve both directions; also preserve distinct
  case-sensitive Terminal keywords and separate highlight priorities.
- [ ] Open Add/Edit log rule and confirm all options, including `Case sensitive`,
  are visible. Check the two-column fields and notification row in dark/light
  themes, at 100%/150% scaling, and with a short window (content should scroll).
- [ ] Confirm all eight checkboxes and their labels share the same vertical
  center in checked, unchecked, and disabled states.
- [ ] Read the explanation below RX trigger sequence: `(none)` disables it;
  with no executable sequence it points to Sequences; with TxOnly it explains
  that RxOnly or Both is required. Select a sequence and match RX with Event
  unchecked: commands should run once, with additional triggers ignored while busy.
- [ ] Set Priority to `100000` and cooldown to `86400`, save, reopen, and reload
  the profile. Both values must remain unchanged. Invalid Priority or cooldown
  outside `5`–`86400` must keep the editor open with an inline error.
- [ ] Switch the editor's Mode to HEX: `Case sensitive` becomes unavailable;
  switch back to Terminal and confirm its previous check state is retained.
- [ ] Open Rules tab.
- [ ] Confirm the rule editor and rule list label the selector as `Mode`, with
  `Terminal` and `HEX` choices (not `Match = Text/HEX`).
- [ ] In Terminal mode, enable one Terminal rule and one HEX rule whose keywords
  both represent the same incoming bytes; confirm only the Terminal rule creates
  events/highlights.
- [ ] Switch to HEX mode without changing either rule's Enabled state and confirm
  only the HEX rule creates events/highlights.
- [ ] Mark both rules as filters and confirm the Filter flyout lists only rules
  for the current mode.
- [ ] Receive matching and non-matching lines before opening Filter. Check both
  rules, select `Apply`, and confirm existing and future lines matching either
  rule are visible while other lines are hidden. Apply `ALL` and confirm hidden
  retained lines return in their original order without Clear.
- [ ] With a large retained history and live RX, rapidly switch filters, change
  capacity/format, or Clear during filtering. Confirm the latest filter wins,
  RX/file logging continue, memory stays bounded, and removed lines never return.
- [ ] Reopen the Filter flyout, change the checks, close it without applying, and
  confirm the active filter selection is unchanged.
- [ ] Select `ALL` and confirm the individual rules are cleared; switching modes
  must fall back to `ALL` if none of the selected filters belong to the new mode.
- [ ] Toggle WARN highlight off.
- [ ] Confirm future WARN lines no longer use the WARN highlight color.
- [ ] Change ERROR highlight color.
- [ ] Confirm future ERROR lines use the new color.
- [ ] Add a highlight rule for `RESET`.
- [ ] Confirm the rule persists after Save Profile and restart.
- [ ] Receive a Terminal line, switch the retained RX view to HEX, and confirm
  HEX highlight/filter rules match its raw bytes in the rebuilt view.

## Search

- [ ] Hide timestamps and RX/TX prefixes, receive `SYS READY`, `RX < READY`,
  `[12:34:56] READY`, and a payload with leading spaces. Search each entire
  payload and confirm neither the match nor its leading text is lost.
- [ ] After searching, change the query/options or timestamp/direction display.
  Press F3/F4 before refreshing: confirm the old result selection does not move.
- [ ] Keep receiving while a large search runs and confirm its result snapshot
  is marked stale when newer logs were excluded.
- [ ] Search more than 1,000 matching lines. Start with Shift+Enter and cross
  page boundaries with F3/F4: confirm the list page and selected row follow the
  original-log position. Press Enter on a selected result to jump to it.
- [ ] Search a combining accent, Korean text, emoji, and tab-containing text;
  confirm selection covers the intended terminal cells and nearby logs remain visible.

- [ ] Search for `WARN`.
- [ ] Confirm match count updates.
- [ ] Receive `RX < \tREADY` with a real tab, search for `READY`, and confirm
  the rendered word is selected even though the tab expands to multiple cells.
- [ ] Search for a term that includes a literal tab and confirm the expanded
  tab cells on the target occurrence are selected.
- [ ] Receive `\t\tREADY`, search for a literal tab, and confirm the first and
  second tab remain separate results and select separate tab-stop ranges.
- [ ] Size the terminal so its cursor reaches the right margin, receive
  `\tREADY` (and then `\t\tREADY`) from that position, and confirm searching
  for `READY` selects the word even though the preceding tabs render no cells.
- [ ] Size the terminal so a CJK character or emoji soft-wraps from the final
  column, place a real tab and `READY` after it, and confirm `READY` jumps to
  the correct rendered cells rather than counting the wide-wrap placeholder
  as another tab.
- [ ] With a tab before the payload text, case-insensitively search Unicode
  pairs such as `ſ`/`S`, `K`/`K`, and `ß`/`ẞ`; confirm result count and jumps
  follow .NET `OrdinalIgnoreCase` rather than JavaScript case folding.
- [ ] Receive a payload ending in `READY ` and confirm a search including the
  trailing space both counts and jumps to the match.
- [ ] Send `open_rx_mq() ...  rx_mq`, search for `rx_mq`, and confirm both
  occurrences on the same logical line are counted and selected in order.
- [ ] Search for ` rx_mq` (including the leading space) and confirm only the
  right-hand occurrence is counted; save/reload the profile and confirm the
  leading space is preserved.
- [ ] Click Next and Prev.
- [ ] Confirm the magnifying-glass Search button sits immediately left of Prev,
  within the existing search field area, without increasing toolbar padding or gaps.
  Click it and compare with Enter: both refresh the visible-log snapshot using the
  current options, select the next match, and add the query to search history.
- [ ] Confirm xterm jumps/selects matches.
- [ ] Append additional matching logs, then use Next/Prev and F3/Shift+F3/F4;
  confirm the total match count remains the last Enter-search snapshot.
- [ ] With Auto Scroll OFF and timestamp/direction prefixes hidden, receive an
  unterminated tab-only RX segment. Search and navigate older matches; confirm
  the selected text belongs to the requested log line, including repeated text.
- [ ] While RX has an unfinished line, change a display setting that redraws
  the logs. Search the first retained log; confirm its text and match offsets
  are unchanged. Clear during partial RX, then confirm new logs start cleanly.
- [ ] Press Enter in the search box and confirm the total match count updates to
  include the newly appended matches.
- [ ] Click the xterm log and press Enter; confirm Auto Scroll toggles once.
- [ ] Hold Enter while xterm is focused; confirm key repeat does not toggle it repeatedly.
- [ ] Focus the TX input and press Enter; confirm the command sends without toggling Auto Scroll.
- [ ] Toggle Case and confirm behavior changes.
- [ ] After a completed search, edit the search text or toggle `Aa`, `|ab|`, or
  `.*`; confirm the existing result rows remain visible as stale until Enter or
  Refresh runs the new search. Confirm F3, page navigation, and result jumps are
  blocked while those rows are stale, with a refresh-first message for a result
  jump attempt. Clear the input completely and confirm either Enter or Refresh
  clears the retained rows.
- [ ] Start a search on a large snapshot, edit the search text before it
  finishes, and confirm the old result rows, page count, and selected match stay
  together; confirm no canceled-search xterm selection occurs.
- [ ] With valid regex results visible, enter an invalid regex such as `[` and
  press Enter; confirm the prior rows and snapshot remain visible as stale while
  the readable regex error is shown. Correct the pattern and refresh to replace
  them normally.
- [ ] Toggle Whole Word and confirm `error` matches `error-error` but not
  `errorCode` or `preerror`.
- [ ] With Whole Word enabled, search `a-a` in `xa-a-a`: confirm the trailing
  `a-a` is found with Regex both off and on.
- [ ] Toggle Regex, search for `id=\d+`, and confirm variable-length matches
  are counted, highlighted, and selected at the correct xterm range.
- [ ] Combine Case, Whole Word, and Regex and confirm all three constraints
  apply to the same result snapshot.
- [ ] Enter an invalid regex such as `[` and confirm a readable search error is
  shown while RX and UI rendering continue normally.
- [ ] Search with a zero-length-only regex such as `^` or `(?=a)` and confirm it
  reports zero matches rather than creating an unselectable phantom result;
  confirm a consuming anchored regex such as `^WARN` still works.
- [ ] In the search box, press Alt+C, Alt+W, and Alt+R and confirm the matching
  option toggles once without changing the search text.
- [ ] Search for a timestamp-only value or direction marker such as `RX <` and
  confirm it does not match unless the same text exists in the payload itself.
- [ ] With a narrow log viewport, create long wrapped matching lines, leave the
  results stale while newer matches append, and confirm an old result jumps to
  the same logical line or reports no hit after that line leaves scrollback.
- [ ] Confirm logs continue appending during search.
- [ ] With 200,000 retained lines containing two matches each, confirm the
  total is 400,000, each matching line appears once, only 1,000 result rows
  are materialized per page, search can be canceled by changing the term, and
  the UI remains responsive.

## Search Results Tab

- [ ] Open Search tab.
- [ ] Confirm result rows are compact and stable.
- [ ] Confirm repeated matches on one line appear as one row labeled `×N` and
  double-clicking the row jumps to the first occurrence on that line.
- [ ] With more than 1,000 matching lines, confirm `Prev` moves toward older
  pages and `Next` moves toward newer pages while each page stays at or below
  1,000 rows.
- [ ] After new logs mark a multi-page result snapshot stale, move between its
  existing pages and confirm the stale indicator remains until a refresh builds
  a new snapshot.
- [ ] On the newest results page, append more matching logs and click `Next`;
  confirm the search refreshes and moves to the newest available page.
- [ ] Confirm status appears in the toolbar, not as a result row.
- [ ] Confirm there is no Auto refresh control and Search results update only after Refresh.
- [ ] Click Refresh and confirm results rebuild.
- [ ] Double-click a result and confirm xterm jumps/selects it.
- [ ] Confirm stale/manual status is clear while logs append.

## Settings Validation

- [ ] While connected with LOG OFF, change the save folder by typing or Browse.
  Confirm no reconnect is required, start logging and verify the file is in the
  new folder. While LOG ON, both folder editors must be disabled.
- [ ] Enter a slow or unavailable network log folder. The window must remain
  responsive, validation must report failure, and the last valid folder must
  remain selected. Repeated retries must not accumulate filesystem workers.
- [ ] Change the log folder or file name with LOG OFF. Confirm the apply status
  says the next logging start and its pending count clears after logging starts.
  Load or reset a profile and confirm earlier pending settings are cleared.
- [ ] In HEX mode, the TX ending selector must be disabled both in the toolbar
  and Settings. Return to Terminal and confirm the previous ending is retained.
- [ ] Toggle timestamp visibility with retained logs and no incoming RX. Existing
  timestamps must disappear/reappear immediately; the tooltip must describe this.
- [ ] Enter invalid numeric values such as `abc`, `-1`, `0` where invalid, and
  an extremely large number.
- [ ] Confirm the app does not crash.
- [ ] Confirm a clear validation/status message appears.
- [ ] Confirm invalid values are not saved as active settings.
- [ ] Enter an invalid save directory and confirm it fails gracefully.
- [ ] Confirm safe settings still apply after validation failures.

## Profile Save, Load, Reset

- [ ] Change baud rate or TX ending.
- [ ] Disable one highlight rule.
- [ ] Add a saved command.
- [ ] Save Profile.
- [ ] Restart the app.
- [ ] Confirm saved settings are restored.
- [ ] Load Profile while disconnected.
- [ ] Confirm Load Profile is disabled or guarded while connected.
- [ ] Reset to Defaults while disconnected.
- [ ] Confirm safe defaults are restored.

## Log File Actions

- [ ] Without opening the Log tab, find **Export logs...** / **로그 내보내기...** immediately
  to the right of **Start Logging / Stop Logging**. Confirm its icon and text
  remain visible when the inspector is collapsed and the window is narrow.
  Verify it uses the same save action in English and Korean, including while
  live file logging is on. Receiving and the log view must stay usable while
  the non-modal progress panel is shown.

- [ ] With LOG OFF, receive MOCK data and use **Export logs...** in the
  Log tab and the LOG button's right-click menu. Verify the UTF-8 file contains
  earlier RX, TX, MARK and system records with timestamps. Repeat in Korean UI.
- [ ] Hide records with a view filter, then save retained logs. Verify hidden
  records are included. Repeat after retention rollover/Clear and confirm deleted
  history is not exported. Cancel the save dialog and verify no error is shown.
- [ ] While receiving, press Export logs and leave the save dialog open as new
  records arrive. Confirm the suggested name contains the first retained record's
  timestamp and the button-click time (both local dates/times, to milliseconds).
  Confirm the output stops at the click-time snapshot, excluding records received
  while the dialog was open. Repeat across midnight and after retention rollover;
  the first timestamp must refer to retained history, not discarded session history.
- [ ] While receiving and saving a live log, export history to a separate file.
  Verify receiving/live logging continue and the export is a stable snapshot.
  Try the active log path and a read-only destination; verify a clear error,
  intact existing files, and that another export can be attempted.
- [ ] During a slow export, verify the progress panel stays inside the upper-right
  log viewport and leaves TX input/send and inspector controls available. Cancel
  a new destination and confirm neither an empty output nor a temporary file is
  left behind. Retry and verify the completed output and live logger keep their
  correct contents. See [GUI validation evidence](log-export-ux-validation.md).
- [ ] Export a large history (up to 1,000,000 records) while MOCK stress is running.
  Verify the progress panel shows percentage and written/total records, the terminal
  and RX counters advance, and tabs/controls remain usable. Completion must not show
  100% before the destination is replaced. Repeat in Korean and English.
- [ ] Cancel a running history export over an existing file. Verify the panel shows
  cancellation until cleanup finishes, the old file is preserved, no temporary file
  remains, and another export succeeds. Close the app during export and verify the
  normal RX/live-log shutdown still completes promptly.

- [ ] Connect to `MOCK`.
- [ ] Wait until serial log file is created.
- [ ] Generate an event and confirm it remains available in Events; double-click
  to inspect its original log while it remains in the visible buffer.
- [ ] Open the Log tab.
- [ ] Open current serial log.
- [ ] Open logs folder.
- [ ] Copy serial log path and paste into Notepad.
- [ ] Confirm no event-log open/copy action is shown.
- [ ] Confirm FileWriter remains running.
- [ ] Confirm EventDetector remains running.

## Mock Stress Mode

- [ ] Select `MOCK`.
- [ ] Start stress mode at a low rate.
- [ ] Confirm logs append and counters increase.
- [ ] Confirm missing sequence count stays 0.
- [ ] Increase lines/sec.
- [ ] Confirm the app remains responsive.
- [ ] Pause rendering.
- [ ] Confirm RX/File/Event counters continue increasing.
- [ ] Resume rendering.
- [ ] Stop stress mode.
- [ ] Reset stress counters.

## Physical COM Packet And Timeout Stress

- [ ] Confirm the `COM4 <-> COM5` com0com pair is free.
- [ ] In the sender instance, connect to MOCK, bridge to `COM4`, select
  `Visual HEX 3-5 ms`, and start stress.
- [ ] In the receiver instance, connect to `COM5` at 460800 baud in HEX mode and
  apply a 15 ms HEX timeout; confirm it sees
  2-6 packets per group with a new group after each long gap.
- [ ] Confirm every correct logical line starts `AA 55 F1`, stays on one group
  number, and ends with its `F3` packet; treat lines starting `F2/F3` as splits
  and an `F3 -> next F1` pair on one line as a merge.
- [ ] For the standalone tool test, close both instances, reopen the receiver on
  `COM4`, and run the stress tool on `COM5` in timeout mode using the command in
  `docs/com0com_stress_testing.md`.
- [ ] Confirm 3-5 ms packets stay in one HEX group until each 32-40 ms gap.
- [ ] Confirm the next packet after the long gap starts a new HEX group.
- [ ] Confirm RX/file counters rise, UI remains responsive, and serial and
  connection error counters stay at zero.
- [ ] Repeat in load mode for at least two minutes and verify the tool's final
  actual-gap and throughput summary.
- [ ] Close Serial Monitor and run the opt-in automated native-boundary test.

## Health And Footer Status

- [ ] During sustained mock event bursts, confirm the event list updates without
  entry/exit fades, retains the configured visible limit, and follows the latest
  event only when auto-scroll is enabled and the pointer is outside the list.
- [ ] Select a retained event during a burst and verify its selection stays on
  that event; verify safe selection clearing when it leaves the buffer.
- [ ] Copy footer status during a burst and confirm `Event UI` reports pending
  items, batch count, maximum batch size, queue drops, and last/maximum synchronous
  batch time. These times exclude deferred XAML layout, paint, and scrolling.
- [ ] Overload the event UI queue in mock mode and verify a nonzero queue-drop
  count appears in the footer UI-drop total and `UI event list updates dropped`
  appears in health details. Normal visible-history eviction must not produce
  this warning. Check event detection and file logging separately for loss.

- [ ] Confirm bottom status shows `HEALTH OK` during a clean mock session.
- [ ] Confirm hovering over the footer shows the current health reasons.
- [ ] Right-click the footer, copy status, and paste it into Notepad.
- [ ] If practical, force a warning/error counter and confirm health changes to
  `WARNING` or `ERROR`.
- [ ] Trigger a recoverable validation error and confirm it does not leave Health
  permanently at `ERROR`.
- [ ] Recover or restart a failed background service and confirm its active
  background error clears from the footer reason list.
- [ ] Force two different observer loops to fail and confirm both reasons remain
  independently visible; a service status change must not clear either reason.
- [ ] Confirm an observer reason clears only after that observer task is actually
  created again. Bridge observer failures should remain until app restart.
- [ ] Accumulate more than five health reasons and confirm the tooltip shows five
  plus a remaining count while copied status includes every reason.
- [ ] Confirm stale profile or search-result states are not treated as health
  errors.

## Time/position navigation

- [ ] Confirm the terminal uses its full available width and height without a time rail, activity/peak graph or Navigate inspector tab. The fixed log toolbar must show Go to in both Korean and English, including when the other toolbar controls are scrolled.
- [ ] With an empty buffer, open Ctrl+G and verify helpful empty-state validation with disabled time fields/Go. Receive logs, close/reopen and confirm the range becomes available. Ctrl+G and the toolbar's Go to button must open the same native dialog; the log context menu has no separate navigation item. Escape/Cancel must restore focus without moving the log.
- [ ] Open Ctrl+G at a historical position and confirm the corresponding time is selected with focus on Minutes. Change Hours/Minutes/Seconds by mouse wheel and native keyboard arrows. Open a dropdown and verify its list keeps normal wheel scrolling. The dialog and button positions must remain stable.
- [ ] Receive 200 logs with an identical millisecond timestamp, open Ctrl+G after that timestamp, and select a later idle time: Go must target log 200 by its frozen ID. Receive more logs while the dialog is open and repeat; it must still target the original log 200. If that ID is evicted, show an expired-position message without jumping to another log. An ordinary request exactly at the shared timestamp must still target its first matching log.
- [ ] Minimize/restore or close the window while Ctrl+G metadata is loading: the canceled opening must not show a stale empty dialog. A later Ctrl+G must open a fresh session; an already-open dialog must retain its selection across minimize/restore.
- [ ] Compare RX/file counts with the native dialog closed, open, and while changing selectors. Keep width-reflow timing separate: the current bundled xterm can spend several seconds changing width with one million retained rows; normal height changes must reuse the metadata rings.
- [ ] Open on a single-day range and confirm its date is fixed text. Repeat across midnight, month/year boundaries and leap day; native Year/Month/Day choices must expose only valid dates/times within the captured range.
- [ ] Keep receiving while the dialog is open: the range, selector lists and chosen time must stay fixed. Close/reopen to capture a fresh oldest-retained-to-current-wall-clock range. No polling or text preview should compete with selector changes.
- [ ] During a long RX idle interval, select a time after the last recorded log and press Go; it must land on the last retained log. Choose an interval between recorded timestamps and confirm it lands on the first log at or after that time.
- [ ] In a small window, check the date/time fields, two-line range and error text without raw JSON/Int64/conversion messages. Full trimmed status must remain available by tooltip, and reopening must clear previous errors.
- [ ] Turn visible timestamp prefixes off and confirm Ctrl+G still navigates by the retained internal timestamps.
- [ ] Save timestamp display OFF, restart into an empty log view and receive the first logs without changing any setting. The empty startup restore must complete and Ctrl+G must be usable immediately after the first batch. Repeat with timestamp display ON and through later ON/OFF changes; a presentation change must never be required to enable navigation.
- [ ] With no real timestamp metadata, Ctrl+G time fields/Go must be disabled with a helpful message while available Back history still works.
- [ ] Receive new RX after a jump and confirm the chosen viewport stays in place, Auto Scroll is off, RX/file logs continue, and view pause is unchanged.
- [ ] Use Back from the Ctrl+G dialog repeatedly, including a wrapped log offset before and after column resize. After retention eviction, an expired Back anchor must report unavailable rather than jump elsewhere.
- [ ] With one million retained lines, drag/resize the window and open the time selector from both Ctrl+G and the toolbar. No histogram requests, repeated whole-buffer scans, unbounded history, or frozen receive controls.
- [ ] Verify Tab/Shift+Tab, arrow keys and Enter operate native date/time fields without toggling terminal Auto Scroll. Escape must close the native dialog or its open dropdown. No HTML popup or browser-native controls should appear.
- [ ] Verify Korean/English, light/dark/high contrast, narrow widths, clear/mode change, full screen restore, and a system clock regression.

## AI Plugin / CLI / MCP

- [ ] Confirm Terminal/HEX is fixed to the left of Port, including when the connection toolbar is scrolled. Open each mode dropdown and check the short Terminal/HEX item tooltips in Korean/English.
- [ ] Use CLI `mode`, `mode --set hex`, and `mode --set terminal`, or MCP `serial_set_mode`. Check status.mode, both GUI selectors and active rule mode; mode changes clear visible logs/search without deleting saved logs. A mode change sends no bytes and does not open/close COM. Explicit CLI/MCP TX formatting remains independent.
- [ ] During AI capture, GUI TX, sequences, bridge startup/activity and reconnect, mode mutations must be busy and the GUI mode selectors disabled. After completion/cancellation, mode selectors must recover.
- [ ] Confirm the old MCP menu is named Plugin. Check quick start, client installation/update, usage examples and troubleshooting in Korean/English, narrow panels and light/dark themes.
- [ ] In Plugin quick start and About, confirm GitHub is the primary installation source and `docs/agent_plugins.md` is the guide. ZIP/local installation does not appear in the quick-start flow.
- [ ] Install from `https://github.com/Establers/custom-serial-monitor.git` independently in Codex desktop/CLI, Claude Code and Copilot CLI. Confirm the plugin skill and MCP tool schemas are available in a new session. Verify Copilot VS Code with agent plugins enabled; do not treat a Codex IDE MCP fallback as a plugin installation.
- [ ] Update the published Git plugin through each host. Confirm marketplace listing refresh versus installed-plugin update follows the selected host.
- [ ] Upgrade the GUI in place and reconnect MCP. Confirm the Git-installed plugin remains managed by its Git source. Separately test `<app>\agent-plugin` as a development/offline fallback and apply local changes through the host's reload/restart.
- [ ] Install to a path containing spaces/Korean and to another drive. Test launcher discovery from the installed registry and from a nearby portable app; test `SERIAL_MONITOR_CLI` with AI launched from that actual shell.
- [ ] Run `serialctl describe --json` and MCP `serial_get_capabilities` without a running GUI. Confirm supported tools/versions are returned with no COM access or TX. Test missing/old CLI diagnostics without contaminating MCP stdout.
- [ ] Keep an old standalone skill and manual server registration while migrating. Confirm setup does not delete them; intentionally remove only superseded duplicates after read-only plugin verification.
- [ ] Run the new GUI, then `serialctl instances`; select `--instance` when two GUIs are running.
- [ ] Connect through the GUI, then `serialctl request --port COM7 --text "status" --duration-ms 2000`.
  Confirm actual RX appears in both the GUI and the capture; interleaved output is preserved without matching.
- [ ] Pause/filter the GUI and switch Terminal/HEX modes; captures must continue independently.
- [ ] Capture more than 16 KiB. Follow every `page.nextOffset` until `hasMore=false` and compare hex with `.bin`.
- [ ] During a capture, GUI TX/sequence/bridge start and a second AI capture must be busy; status remains readable.
- [ ] Use `AI 중지`, CLI cancel, MCP cancellation, client exit, and physical disconnect; control must be released.
- [ ] Force disk/queue failure and verify explicit capture failure or rxGapDetected without stopping serial RX.
- [ ] Register `serialctl mcp` in an AI CLI and let it read saved commands, execute one, inspect all RX, and select the next command.
- [ ] While bridge startup is waiting for its virtual port to open, AI control must return busy; startup failure/cancellation must release control.
- [ ] During AI connect/port refresh, GUI connect and connection-setting controls must be disabled. Verify success only for the requested port and baud.
- [ ] Cancel AI connect during port enumeration, lifecycle-gate wait and port opening. No delayed connection may start; after a successful connect, closing the caller must leave the session connected.
- [ ] Keep an old capture's bin/txt/json open while starting capture 33. The new capture must succeed, every file of the pinned capture must remain readable, and another old capture must be evicted.
