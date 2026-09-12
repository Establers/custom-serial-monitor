# HEX 간헐 지연 검토 (2026-09-11)

이 문서는 초기 검토 이력이다. 이후 사용자가 승인한 UI 성능 수정과 최신 측정 결과는 [HEX 성능 개선 결과](hex_performance_changes.md)를 참고한다.

## 결론

가장 우선적인 병목은 UI 스레드의 중복 HEX 변환과 항목 수만으로 제한하는 배치 처리다. 별도로 연속 수신의 프레이밍 대기, 드래그 선택의 전체 이력 탐색도 지연 후보다. 실제 장치 증상을 재현하거나 프로파일링한 결과는 아니며, 코드 검토와 격리된 성능 측정 결과를 구분한다. 이번 검토에서는 제품 코드를 변경하지 않았다.

## 1. UI 변환 비용과 임시 메모리 할당 — 측정으로 확인

- `LogPipeline.FlushHexSegmentAsync`에서 이미 HEX `DisplayText`를 생성한다.
- `LogViewModel.FormatDisplayText`는 이를 재사용하지 않고 `RawBytes`를 다시 변환한다.
- `FormatPartialRxVisibleSegment`의 첫 조각은 길이 계산용 변환 후 `FormatPlainSafeDisplayLine`에서 같은 바이트를 다시 변환한다.
- `FormatRawBytesAsHex`는 바이트마다 `ToString("X2")`로 작은 문자열을 생성한다. 배경 디코더 역시 LINQ와 바이트별 문자열 생성을 사용한다.
- `MainViewModel`의 UI 배치는 16ms 간격으로 최대 40항목, 대기 1,000항목 이상이면 400항목을 처리한다. `SmoothVisualAppendMaxChars = 32 * 1024`는 표시용 속성에만 연결되어 있고 실제 `UiBatchDispatcher`의 배치 제한에 사용되지 않는다.
- WebView 전송 단계의 분할은 `Log.AddRange` 이후이므로 이 UI 가공 비용을 제한하지 못한다.

현재 소스의 LogViewModel 및 의존 파일을 별도 .NET 8 Release 콘솔에 직접 연결해 측정했다. 64KiB의 ASCII A 바이트를 가진 연속 partial RX 조각을 한 번의 AddRange로 전달하고, 각 조건 6회 중 첫 실행을 제외한 5회를 사용했다. 배치 생성은 측정 밖, WebView 렌더링과 실제 수신은 제외했다. Terminal 조건도 동일한 partial 조각을 사용해 화면 가공 경로만 비교했다.

| 조각 수 | Terminal 중앙값 | HEX 중앙값 | HEX 최대 | HEX 스레드 할당량/호출 |
|---:|---:|---:|---:|---:|
| 1 | 0.18ms | 2.78ms | 12.75ms | 7.38MiB |
| 40 | 3.14ms | 49.61ms | 60.74ms | 173.15MiB |
| 400 | 33.16ms | 354.23ms | 412.01ms | 1,703.29MiB |

이는 큰 연속 수신과 backlog를 가정한 부하 측정이지 평상시 작은 패킷의 평균 지연이 아니다. 할당량은 누적 생성량이며 상주 메모리나 메모리 누수의 측정값이 아니다. GC에 의한 실제 멈춤 시간은 별도 추적이 필요하다.

권장 수정: 생성된 HEX 표현 재사용, 바이트별 임시 문자열 없는 공통 포매터, UI 배치의 문자/바이트 예산 도입. 단일 대형 항목의 비용도 제한해야 한다. 패킷 경계와 파일 원본 로그는 보존해야 한다.

## 2. 연속 수신이 늦게 표시되는 현상 — 코드상 조건 확인

`LogPipeline`의 HEX 경로는 idle 경계 또는 64KiB 누적 시 출력하며, 터미널처럼 250ms 주기 partial 출력이 없다. idle보다 짧은 간격으로 데이터가 계속 도착하면 64KiB까지 화면 표시가 지연될 수 있다. 예를 들어 파이프라인에 도달하는 115200bps, 8N1 연속 데이터가 이상적인 속도로 쌓이면 약 5.69초 분량이다. 실제 native ReadFile 완료 크기와 시점에 따라 관찰값은 달라진다.

정정: native idle 옵션을 켠 서비스 경로는 inter-byte timeout과 total timeout 0을 사용하지만, 현재 MainViewModel의 실제 연결/재연결은 `CreateLiveModeReceiveOptions`에서 `UseNativeIdleTimeout = false`로 설정한다. 따라서 현재 앱은 즉시 배출 수신과 애플리케이션 timeout 조합이다. 앞선 native 대기 설명을 현재 앱의 직접적인 지연 원인으로 적용해서는 안 된다. UI 버튼은 정상인데 데이터만 몰려 나타나면 파이프라인의 그룹 누적을 우선 확인한다.

사용자 후속 제약: timeout 로직은 성능 개선의 변경 대상에서 제외한다. 시간 기준 partial 출력 제안도 이번 개선 범위에서 제외한다. 기존 idle 값, 경계 판정, 64KiB 출력 시점, 모드 전환 및 설정 변경 동작을 보존하고 UI 가공 비용만 개선해야 한다.

## 3. 수신 중 드래그 선택 — 조건부 후보

`Assets/xterm/index.html`의 `onTerminalWriteParsed`는 논리 행 캐시를 무효화한다. 드래그 중 힌트 계산은 `getSelectionDeltaMilliseconds` → `getLogicalLineStartRows`로 이어지고, 캐시가 없으면 전체 사용 이력을 순회하고 buffer.length 크기의 Int32Array를 만든다.

2,000행 선택 제한은 이 전체 탐색 뒤에 적용되므로 탐색 비용을 막지 못한다. 강제 갱신에서는 큰 선택 영역의 문자열 추출과 HEX 바이트 집계도 실행된다. 수신 중 선택/드래그에서만 렉이 난다면 우선순위가 높다. 브라우저 성능 추적으로 실제 비용을 확인해야 한다.

권장 수정: 선택 주변만 탐색하거나 논리 행 인덱스를 증분 유지하고, 강제 선택 계산에도 작업량 제한을 적용한다.

## 나머지 검토

- 파일 저장은 별도 작업과 용량 제한 큐로 분리되어 있다. UI에서 직접 디스크 쓰기를 기다리는 구조는 확인되지 않았다. 디스크 지연과 프로세스 공통 GC 영향까지 배제한 것은 아니다.
- 파이프라인 상태 이벤트는 UI 작업을 매번 등록하지 않고 dirty 플래그를 설정한다. 이 경로는 우선순위가 낮다.
- HEX 필터/이벤트 패턴은 미리 컴파일하고 바이트 검색을 한다. 규칙 개수와 패턴에 따른 추가 비용은 있으나 이번 측정은 규칙 없이도 병목을 보였다.
- 화면 및 파이프라인 큐는 항목 수 제한이지만 항목 크기는 가변이다. 대형 HEX 조각에서는 항목 제한만으로 메모리 사용량이 충분히 작다고 볼 수 없다.
- xterm 이력 압축에는 기본 4ms 작업 예산이 있다. 선택/검색/리사이즈에서의 압축 해제 비용과 긴 줄의 줄바꿈 비용은 아직 실측하지 않았다.

## 검증 및 다음 확인

관련 기존 테스트 34개 통과: LogPipelineHexFramingTests, LogViewModelModeSwitchTests, EncodingDecoderTests, LogHistoryRetentionTests, WindowsSerialReadTimingTests.

실행: `dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj -p:RuntimeIdentifier=win-x64 --filter "FullyQualifiedName~LogPipelineHexFramingTests|FullyQualifiedName~LogViewModelModeSwitchTests|FullyQualifiedName~EncodingDecoderTests|FullyQualifiedName~LogHistoryRetentionTests|FullyQualifiedName~WindowsSerialReadTimingTests" --verbosity quiet`

초기 no-restore 실행은 기존 assets의 win10-x64 대상 누락으로 실패했으며, win-x64 대상으로 복원/빌드 후 위 테스트가 통과했다. 기능 테스트 통과는 UI 프레임 지연이 없다는 뜻이 아니다.

실제 증상 시점에 UI AddRange 시간/문자 수, PendingVisualLineCount, HexPendingByteCount, RX 바이트 증가량, xterm append 시간, GC pause를 함께 기록하면 가공 병목·프레이밍 대기·WebView 지연을 구분할 수 있다. 장치 속도/패킷 크기/idle 설정, 선택 여부, 저장 여부를 동일하게 고정해 비교해야 한다.

## 후속 timeout 집중 검토

최신 사용자 기준 및 후속 결과: 199.0932ms/200ms 차이는 허용하며 테스트에 2ms 오차를 반영했다. 아래 엄격 검사 실패 기록은 당시의 진단 이력이다. 현재 중요한 설정 미적용 경로와 최신 테스트 결과는 [timeout 적용 재검토](timeout_application_review.md)를 따른다.

제품 timeout 코드는 변경하지 않았다. UI 성능 개선에서도 timeout 값, 수신 옵션, 경계 판정, 출력 시점은 보존 대상으로 둔다.

확인한 의도:

- 관측 수신 간격이 timeout 미만이면 같은 그룹, 이상이면 다음 그룹이다.
- 대기 시간은 마지막 수신의 Stopwatch 시각에서 계산하며 baud에 따라 사용자 설정을 바꾸지 않는다.
- 64KiB partial 출력은 논리 패킷 종료자를 만들지 않는다.
- native idle 옵션이 명시적으로 켜진 경로의 확정 경계에는 timeout을 한 번 더 기다리지 않는다. 현재 앱의 기본 실제 연결은 native idle 옵션을 끈다.
- 실행 중 timeout 단축은 기존 대기를 깨우고 마지막 수신 이후 경과 시간을 반영한다. 연장은 이전 마감 시간을 취소하고 대기 중 바이트를 유지한다.
- 브리지의 최대 100ms 전송 지연 제한은 별도 전송 분할 정책이며, 화면 HEX 패킷 종료 조건과 혼동하면 안 된다.

새로 확인한 이슈:

`LogPipeline.ProcessAsync`의 HEX `Task.Delay` 완료 분기는 입력 큐를 확인한 뒤 바로 `FlushHexGroupAsync`를 호출한다. 실제 경과 시간이 설정 timeout 이상인지 다시 검사하지 않는다. 남은 시간이 소수 밀리초인 타이머의 해상도/반올림 영향이 조기 마감으로 이어질 수 있다. 반복 테스트에서 **200ms 설정이 199.0932ms에 마감**되는 것을 재현했다. 타이머 정밀도가 원인이라는 해석은 코드 경로에 근거한 추론이며, 직접 확인한 사실은 설정 시간보다 빠른 마감이다. 실제 두 패킷의 오분리는 아직 하드웨어에서 재현하지 않았다.

기존 `ChunkWithoutNativeIdleBoundary_RetainsApplicationTimeoutFallback` 테스트는 100ms에 미완료인 것을 본 뒤 입력을 닫으므로 500ms 자연 timeout의 정확한 마감을 검증하지 못했다. 새 `LogPipelineLiveTimeoutTests`는 입력을 열어 둔 채 20/100/200ms를 각각 반복 검증하고 단축/연장을 추가 검증한다.

검증 결과:

- Core 전체 35개 통과.
- timeout/수신/브리지/설정 관련 기존 WinUI 테스트 50개 통과.
- 신규 최종 테스트 5개 중 4개 통과, 200ms 반복 자연 마감 1개 실패(199.0932ms, 14번째 샘플).
- 초기 단발 검사는 통과하기도 했으므로 반복 검사를 남겼다. 테스트가 드러낸 문제를 숨기기 위해 허용 오차를 완화하지 않았다.

권장 후속 조치는 타이머 완료 후 실제 idle 경과 시간을 재검증하는 최소 수정이다. 이는 timeout 정확성 수정으로 별도 취급해야 하며 이번 검토에서는 적용하지 않았다. 기존 렉과 이 조기 마감 이슈가 동일 원인이라는 증거는 없다.
