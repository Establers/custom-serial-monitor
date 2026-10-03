# COM A → 시리얼 모니터 → COM B / COM C 패킷 경계 검토

검토일: 2026-10-03

## 결론

COM B에 한 번 쓴 데이터가 COM C에서 한 번의 읽기로 전달된다는 보장은 없다.
여러 쓰기가 한 읽기에 합쳐지거나, 한 쓰기가 여러 읽기로 나뉘어도 바이트 내용과
순서가 같으면 정상적인 스트림 전달이다. 해석 프로그램은 수신 버퍼를 누적하고
헤더·길이·종료표식 등 프로토콜 규칙으로 패킷을 복원해야 한다.

별도로, 앱의 브리지에 **대기열 처리 지연을 수신 공백으로 오인하여 추가 분할하는
문제**가 있었다. 이 문제는 재현하고 수정했다. 사용자가 실제로 경험한 모든 현상이
이 문제 때문인지는 외부 해석 프로그램 및 실제 장비와 함께 검증해야 한다.

## 현재 전달 방식

- `SerialService`는 원본 수신 바이트와 수신 완료 시각을 브리지에 전달한다.
- Terminal 모드는 원본 수신 조각을 전달한다.
- HEX 모드는 앱의 HEX timeout을 브리지의 묶기 기준에도 적용한다.
  따라서 timeout 40ms에서 PC에 관측된 그룹 간 간격이 5~10ms이면 함께 묶일 수 있다.
- 브리지는 인식한 idle 경계 뒤에 설정된 timeout만큼의 최소 쓰기 간격을 요청한다.
  원래 회로에서 관측한 패킷 간 무통신 시간을 정확하게 복제하는 기능은 아니다.
- 그룹당 1MiB 및 100ms 제한 때문에 긴 연속 수신도 여러 쓰기로 나뉠 수 있다.
  크기·지연 제한에 따른 분할에는 별도의 idle 경계 간격을 추가하지 않는다.
- 전송에는 RJCP의 버퍼링된 `WriteAsync`를 사용한다. 쓰기 완료 시각이 COM C의
  수신 완료 시각을 의미하지 않으며, 외부 프로그램의 읽기 크기도 강제하지 않는다.

가상 포트와 PC의 수신 시각에는 USB 버퍼링, 드라이버 및 스케줄링의 영향이 있다.
회로에서 패킷 간 간격이 5ms 이상이어도 PC에서 같은 간격이 관측된다는 보장은 없다.
FTDI Latency Timer 2ms 및 앱 HEX timeout 4ms는 이 영향 자체를 제거하지 않는다.

## 재현한 문제와 수정

기존 `RunVirtualWriterAsync`는 첫 조각을 꺼낸 직후 현재 시각으로 timeout을
판단했다. 이미 다음 조각들이 대기열에 있어도 먼저 그룹을 닫았다.
예를 들어 수신 시각이 0/1/2ms인 조각들이 같은 패킷에 속해도, 처리 시점이
늦으면 세 번으로 나누고 각 조각 사이에 인위적인 timeout 간격을 추가할 수 있었다.

수정 후에는 이미 대기 중인 조각들의 수신 시각을 먼저 비교한다. 실제로 관측된
공백, native idle 경계, 설정 변경, 크기 및 최대 지연에 따른 분할은 유지한다.
기다리던 타이머가 만료돼도 즉시 그룹을 닫지 않고 대기열과 단조 시계의
마감 시각을 다시 확인한다. 수신 callback의 UI·파일 처리나 외부 프로토콜 해석을
추가하지 않았다.

## 검증 결과

실제 설치된 COM4 ↔ COM5 가상 포트 쌍을 사용했다. 물리 USB-RS485 장비와 사용자의
외부 해석 프로그램은 이번 테스트에 포함되지 않았다.

| 재현 조건 | 수정 전 쓰기 수 | 수정 후 쓰기 수 |
| --- | ---: | ---: |
| timeout 4ms, 1ms 간격의 조각 3개 | 3 | 1 |
| timeout 4ms, 위 그룹 2개를 10ms 간격으로 수신 | 6 | 2 |
| timeout 5000ms, 최대 그룹 지연을 넘긴 대기열의 조각 3개 | 3 | 1 |

쓰기 수는 테스트용 준비 바이트의 쓰기를 제외한 수치다. 최종 회귀 테스트는
준비 바이트를 쓴 뒤 writer를 일시 정지하고 조각들을 대기열에 넣어 150ms 동안
처리를 지연시킨다. 그룹 내부 시각 차이는 1ms, 두 그룹의 시작 시각 차이는 10ms다.
COM C에 해당하는 COM5에서 읽은 바이트 내용과 순서도 확인한다.

브리지 관련 **44개 테스트 통과**: 실제 COM4/COM5 통합 테스트 6개와 큐·그룹 묶기·
간격 재생·브리지 로그 처리 등의 단위 테스트 38개. 기존 양방향 바이너리 전달,
HEX → Terminal 전환, 큐 초과 시 브리지 중단 동작도 통과했다.

초기 검증은 관련 소스를 그대로 참조하는 별도 테스트 프로젝트로 실행했다.
최종 커밋 전에는 업데이트 관련 작업까지 합친 일반 테스트 프로젝트도 Release로
빌드하고 실행하여 **548개 통과**를 확인했다. COM4/COM5 브리지 통합 테스트는
활성화했다. 두 쌍의 가상 포트가 필요한 routing 테스트와 별도 활성화가 필요한
장시간·native stress·성능 테스트는 이번 실행에서 활성화하지 않았다.

```powershell
$env:SERIAL_COM0COM_TEST = '1'
dotnet test SerialMonitor.WinUI.Tests\SerialMonitor.WinUI.Tests.csproj -c Release -p:Platform=x64 --output "$PWD\.codex-build\main-push-checks\"
```

COM4와 COM5를 사용하는 프로그램을 닫고 실행한다. 초기 별도 프로젝트와 실행 결과
`bridge-final.trx`는 Git에서 제외된 `.codex-build/bridge-boundary-review/`에 있다.
최종 통합 실행 결과는 `.codex-build/main-push-checks/main-push.trx`에 있다.

## 해석 프로그램에서 확인할 부분

사용자의 해석 프로그램에는 프로토콜 기준 및 조각 결합 로직이 있다고 확인했다.
그 코드가 제공되지 않아 아래 항목의 실제 구현 여부는 확인하지 못했다.

1. 한 읽기에 완성 패킷이 여러 개 있으면 모두 추출한다.
2. 마지막의 미완성 조각은 다음 읽기까지 보관한다. 다음 조각에 헤더가 없다고 버리지 않는다.
3. 잘못된 길이·CRC·종료표식이 확인되면 다음 유효 헤더로 재동기화한다.
4. PC에서 읽기 조각 사이에 짧은 공백이 생겼다는 이유만으로 미완성 버퍼를 초기화하지 않는다.
   복원 제한시간을 사용한다면 최대 패킷 전송 시간과 PC 전달 지연을 반영한다.

## 근거

- [Microsoft: Serial communications time-outs](https://learn.microsoft.com/en-us/windows/win32/devio/time-outs)
  — 읽기 반환은 요청 크기와 timeout 조건에 영향을 받는다.
- [com0com 공식 ReadMe](https://com0com.sourceforge.net/com0com/ReadMe.txt)
  — 한 포트의 출력을 대응 포트의 입력으로 전달한다. Baud rate emulation은 별도 설정이다.
- 설치된 RJCP.SerialPortStream 3.0.5 XML 문서의 `WriteBufferSize`, `Write`, `WriteAsync`
  — 내부 전송 버퍼를 사용한다.
- `SerialMonitor.WinUI/Services/SerialBridgeService.cs`, `BridgeDeviceChunkGrouper.cs`,
  `BridgeGapReplayer.cs` 및 `SerialMonitor.WinUI.Tests/Com0ComBridgeIntegrationTests.cs`.
