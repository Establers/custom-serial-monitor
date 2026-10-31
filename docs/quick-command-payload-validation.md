# Quick CMD / 수동 TX 원문 보존 회귀 검증

2026-10-31 검증. main 작업폴더에 반영했으며, 별도 워크트리에서 최신 작업폴더 소스를 빌드했다.

## 문제와 수정

Terminal payload `"  ECHO value \t "`가 Quick CMD 생성, 프로필 정규화,
수동/Quick 공통 송신 경로의 `Trim()`을 통과하면서 실제 송신 내용이 달라졌다.
MCP의 기존 Quick CMD 목록 조회도 잘린 command를 반환했다.

| 줄 끝 None 기준 | UTF-8 / wire bytes |
| --- | --- |
| 입력과 기대 결과 | `20204543484F2076616C7565200920` (15바이트) |
| 수정 전 결과 | `4543484F2076616C7565` (10바이트) |
| 수정 후 저장/재읽기 및 송신 경계 | `20204543484F2076616C7565200920` (15바이트) |

이름·대체 표시 이름·단축키의 정규화는 유지하고 `CommandText`를 원문대로 보존한다.
빈 이름과 공백만 있는 Quick CMD 입력을 거부하는 기존 검증도 유지한다.

- `MainViewModel.SendCurrentCommandAsync`: 수동 입력을 원문으로 전달하고 표시 이름만 정리한다.
- `MainViewModel.SendCommandAsync`: Quick/수동 공통 경로에서 nonblank payload를 잘라내지 않는다.
- `MainViewModel.TryNormalizeSavedCommand`: 생성/편집 검증 후 복사한 원문을 보존한다.
- `ProfileService.NormalizeProfile`: SavedCommands의 payload를 보존한다.
- 기존 `list_commands` 어댑터: 저장된 command를 원문 그대로 반환한다.

HEX의 구문상 공백 처리는 기존 `HexPayloadParser`가 담당한다.
Quick CMD별 모드 저장이나 Quick CMD/Rules CRUD API는 추가하지 않았다.
공유 파일에서는 이 변경만 3방향 통합해 포트별 기록·Rules 등 다른 작업을 보존했다.

## 검사 결과

새 `QuickCommandPayloadPreservationTests` 17개 중 수정 전에는 13개가 실패했고,
수정 후에는 전부 통과했다. 기존 Quick CMD·송신·ProfileService 검사를 합친 101개가 통과했다.
main 통합 후 최신 소스에서 독립 리뷰의 원본 재현 검사 1개를 추가해 **102/102 통과**했다.

| 범위 | 검증 |
| --- | --- |
| Quick CMD 프로필 | 실제 ProfileService 저장 JSON과 재읽기 결과 모두 원문 유지 |
| 생성/편집 | 실제 공통 정규화 함수로 공백·탭·한글·emoji·CR/LF 원문 보존, 입력 객체 불변 확인 |
| 수동/Quick 공통 송신 | 실제 SendCommandAsync의 일반 GUI 플래그로 writer에 전달된 바이트 비교 |
| Terminal 줄 끝 | None / CR / LF / CRLF에서 원문 뒤에 선택한 줄 끝만 추가 |
| HEX | 저장 문자열의 공백 보존 및 `00..FF` 256바이트가 줄 끝 선택 4종 모두에서 정확히 일치 |
| 중복 호출 | 각 송신 경계 검사에서 ISerialService.SendBytesAsync를 한 번만 호출 |
| MCP 목록 | Terminal/HEX 모두 저장된 payload 원문과 기존 mode/ending 메타데이터 확인 |
| 독립 재현 | TerminalQuickCommandPersistenceKeepsExactPayloadBytes 통과 |

**검증 범위의 한계:** 일반 송신 경계 검사는 기록용 ISerialService로 바이트를 수집한 직후
호출자 토큰을 취소해 UI 후처리 전에 종료한다. 실제 공통 송신 코드와 인코딩을 실행하지만,
운영체제의 UART write 완료, 성공 후 UI 갱신, Quick 버튼 클릭이나 단축키 입력의 증거는 아니다.
수동 입력의 앞단은 코드 변경과 검토로 확인했으며 SendCurrentCommandAsync 전체의
UI 성공 경로를 별도로 실행한 것으로 계산하지 않았다.

이번에는 실제 GUI의 `+ → Save → Quick 버튼 클릭`, COM3 실기, 사용자 기본 프로필 변경을
수행하지 않았다. 기존 21개 테스트의 평범한 PING 예시만으로는 공백 보존을 확인할 수 없어
위 경계를 별도 회귀 검사로 추가했다.

## 증거와 재실행

- 테스트: `SerialMonitor.WinUI.Tests/QuickCommandPayloadPreservationTests.cs`
- 수정 전: `.codex-build/quickcmd-payload-preservation/before-fix.trx`
- 수정 후 101개: `.codex-build/quickcmd-payload-preservation/after-fix.trx`
- 통합 후 102개: `.codex-build/quickcmd-payload-preservation/integrated-main.trx`
- 변경만 담은 production patch와 통합 해시: 같은 폴더의 `quickcmd-only.patch`, `integration.json`
- 격리 워크트리: `C:/Users/pjh/.codex/worktrees/serial-quickcmd-payload/serial`

저장소 루트에서 일반 회귀 검사를 실행한다. 아래 명령에는 로컬 리뷰용 추가 probe는 포함되지 않는다.

```powershell
dotnet test .\SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Debug -p:Platform=x64 --filter 'FullyQualifiedName~QuickCommandPayloadPreservationTests|FullyQualifiedName~QuickCommandCreationTests|FullyQualifiedName~SerialTransmitIntegrityTests|FullyQualifiedName~ProfileService'
```
