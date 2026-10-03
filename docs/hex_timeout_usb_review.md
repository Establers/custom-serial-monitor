# HEX timeout와 USB-RS485 전달 지연 검토 — 2026-10-03

## 결론

9600bps, 8N1의 연속 전송은 한 바이트당 약 1.042ms다. 하지만 현재 앱의
HEX timeout은 회로 위의 바이트 간격이 아니라 PC에서 관측한 수신 묶음의
간격을 기준으로 한다. 따라서 회로에서 연속인 데이터가 USB/드라이버/수신
스레드 지연 때문에 PC에 20ms 이상 간격으로 노출되면 HEX 줄이 갈라질 수 있다.
사용자가 관측한 20ms에서 분할되고 40ms에서 유지되는 현상과 일치하는 경로다.
실장비에서 각 지연의 기여도를 측정한 결과는 아니다.

사용자가 알려준 바쁜 구간의 실제 패킷 간격은 약 5~10ms다. 20ms와 40ms는
모두 이보다 크므로, 정상 전달 조건에서도 그 패킷들은 같은 HEX 표시 그룹에
들어간다. 40ms는 분할을 줄일 수 있지만 개별 프로토콜 패킷 경계를 보장하지
않는다. 그룹 사이의 실제 휴지 구간까지 40ms보다 짧으면 더 큰 그룹으로 합쳐진다.

## 현재 코드 경로

- `MainViewModel.CreateLiveModeReceiveOptions`는 `UseNativeIdleTimeout = false`를
  반환한다. 일반 연결과 자동 재연결 모두 이 옵션을 사용한다. 연결 중
  Terminal/HEX 전환 및 timeout 변경을 지원하기 위해 transport는 즉시 drain한다.
- `WindowsSerialReadTiming`의 native inter-byte timeout 구현은 존재하지만
  현재 앱의 연결 경로에서는 활성화되지 않는다. native 옵션 테스트 통과를
  앱이 실제 UART 간격으로 그룹을 나눈다는 증거로 사용할 수 없다.
- `BoundaryPreservingSerialPortStream.OnNativeDataReceived`가 RJCP의 native
  read-completion callback에서 `Stopwatch.GetTimestamp()`를 캡처한다.
  하드웨어가 각 바이트에 붙인 타임스탬프가 아니다.
- `LogPipeline.DrainAvailableHexChunksAsync`는 수신 묶음 타임스탬프 사이의
  간격이 설정 timeout 이상이면 이전 HEX 그룹을 닫는다.
- 새 묶음이 채널에 없으면 마지막 수신 타임스탬프부터의 idle timer로 닫는다.
  timer 만료 때 채널을 다시 drain하고 monotonic clock을 확인한다. UI 표시
  시각이나 패킷 전체 길이, baud로 계산한 전송 시간을 마감 기준으로 사용하지 않는다.
- 이미 타임스탬프가 있는 다음 묶음도 RX→pipeline 채널에 전달되기 전에 timer가
  이전 그룹을 닫으면 뒤늦은 전달로 이미 출력된 줄을 되돌릴 수 없다.
  수신 및 로그 채널은 bounded이며, 소비 지연은 앞단으로 backpressure를 전달할 수 있다.
  이 가능성과 실제 USB/driver 지연을 실장비에서 분리 계측하지는 않았다.

## 어댑터 확인

사용자가 제공한 제품은 [RealSYS CNV485U](https://realsys.kr/product/?idx=216)다.
[공식 설명서](https://realsys.co.kr/data/cnv485u_kor_manual.pdf)는 가상 COM 방식의
USB-RS485 변환기임을 설명한다.

[제조사 드라이버 게시물](https://realsys.kr/download/?bmode=view&idx=7402564)에
첨부된 `CDM2123620_Setup.zip`을 읽기 용도로 내려받아 확인했다. 내부 실행 파일의
Authenticode 상태는 Valid이며 서명자는 `Future Technology Devices International Ltd`다.
이는 제조사가 FTDI 드라이버를 배포한다는 근거다. 정확한 실장 칩 종류, 사용자의
설치된 드라이버 버전, 현재 Latency Timer 값까지 확인한 것은 아니다.

FTDI는 [Latency Timer 기본값을 16ms](https://ftdichip.com/Support/Knowledgebase/settingacustomdefaultlaten.htm)로
설명하며, [USB 버퍼와 latency가 데이터 전달 간격에 영향을 준다](https://ftdichip.com/Support/Knowledgebase/an232beffectbuffsizeandlatency.htm)고
설명한다. 이 숫자를 모든 어댑터나 현재 장치의 측정값으로 일반화해서는 안 된다.

## 실행 검증

현재 소스의 Release 빌드로 다음 테스트 클래스의 38개 테스트가 통과했다.

- `LogPipelineHexFramingTests`
- `LogPipelineLiveTimeoutTests`
- `ReceiveModeRuntimeTests`
- `NativeReadBoundaryTrackerTests`
- `WindowsSerialReadTimingTests`

추가로 임시 console에서 실제 `LogPipeline`을 실행했다. 아래는 PC 전달 조건을
합성한 진단이며, 물리 UART/USB 어댑터의 측정 결과가 아니다. 모든 경우에서
입력 바이트와 출력 바이트의 값 및 순서가 일치했다.

| 조건 | Timeout | 출력 그룹 길이 |
| --- | ---: | --- |
| 16바이트씩 전달, 관측 간격 16/32/16ms | 20ms | 32 + 32바이트 |
| 같은 바이트와 같은 관측 시각 | 40ms | 64바이트 |
| 소비 시작이 늦어도 모든 입력이 채널에 있고 관측 간격은 16ms | 20ms | 64바이트 |
| 다음 묶음의 수신 시각은 +8ms이나 채널 전달은 이전 그룹 마감 이후 | 20ms | 16 + 16바이트 |

재현 파일은 Git 제외 경로 `.codex-build/hex-timeout-review/probe`에 보관한다.
Windows App Runtime DLL 검색 경로가 필요하므로 다음과 같이 실행한다.

```powershell
dotnet test SerialMonitor.WinUI.Tests/SerialMonitor.WinUI.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~LogPipelineHexFramingTests|FullyQualifiedName~LogPipelineLiveTimeoutTests|FullyQualifiedName~ReceiveModeRuntimeTests|FullyQualifiedName~NativeReadBoundaryTrackerTests|FullyQualifiedName~WindowsSerialReadTimingTests" --output "$PWD/.codex-build/hex-timeout-review/"
$env:PATH = "$PWD/.codex-build/hex-timeout-review;" + $env:PATH
dotnet run --project .codex-build/hex-timeout-review/probe/HexTimeoutProbe.csproj -c Release
```

## 적용 방향

1. 해당 COM 포트의 장치 관리자 → 속성 → 포트 설정 → 고급에서
   `Latency Timer (msec)`를 확인한다. FTDI 옵션이 있고 현재 16ms라면 2ms로
   줄인 뒤 COM 포트를 다시 열어 같은 부하에서 20ms 설정을 비교한다.
   정확한 옵션명과 지원 범위는 설치된 드라이버에 따라 다를 수 있다.
   이 검토에서는 드라이버를 설치하거나 시스템 설정을 바꾸지 않았다.
2. 40ms에서 원하는 표시 그룹이 유지된다면 당장의 표시 설정으로 사용할 수 있다.
   실제 패킷 간격이 5~10ms일 때의 병합은 설정 정의상 정상이며, timeout을
   올리면 서로 다른 그룹까지 합칠 수 있다는 점을 같이 확인한다.
3. 어댑터가 보존하지 않은 회로상의 간격을 앱이 정확히 복원할 수는 없다.
   개별 패킷 경계가 반드시 맞아야 하는 경우 헤더/길이/종료표식 등 프로토콜
   정보로 framing하거나 하드웨어의 수신 시각 정보를 받아야 한다.
   native timeout을 켜는 것만으로 USB 이전의 타이밍이 복원되지는 않는다.

이번 검토는 제품 timeout 알고리즘을 변경하지 않았다. 설정을 임의로 늘리거나
서로 다른 패킷을 휴리스틱으로 다시 합치는 변경은 검증 없이 도입하지 않는다.
