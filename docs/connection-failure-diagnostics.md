# Connect 실패 원인과 포트 점유 조회

수동 **Connect**가 실패하면 기존 연결 실패 창에 원인 안내, 확인된 프로그램 이름/PID,
Windows 오류 코드(확인 가능한 경우), 원본 오류 메시지를 함께 표시한다.
본문은 스크롤·텍스트 복사가 가능하고 최근 실패 내용은 기존 연결 진단에도 남는다.

| 상황 | 안내 |
| --- | --- |
| 포트가 사라짐 / 경로 없음 | 포트 목록 새로고침, USB·드라이버 확인 |
| 접근 거부 | 점유·접근 권한·드라이버 가능성을 구분하고 확인 가능한 점유 프로그램 표시 |
| 공유 위반 / 사용 중 보고 | 확인된 프로그램 이름/PID와 그 프로그램의 포트 연결을 닫으라는 안내 |
| 장치 사용 불가 / 드라이버 오류 | 장치·USB 연결·드라이버 확인 |
| 열기 시간 초과 | 장치·드라이버와 이전 연결의 정리 상태 확인 |
| 잘못된 설정 / 미지원 설정 | 요청한 시리얼 설정 확인 |
| 이벤트 처리/로그 파이프라인 등 세션 시작 실패 | 원본 오류 표시. 포트 점유 오류로 오인하거나 점유 조회를 하지 않음 |

## 동작 범위

- 정상 연결에는 조회를 실행하지 않는다. 자동 재연결의 각 재시도에도 보조 프로세스를 만들거나
  실패 창을 띄우지 않는다. 기존 자동 재연결 정책과 RX/TX 처리 경로는 유지한다.
- **접근 거부만으로 다른 프로그램이 점유했다고 단정하지 않는다.** 포트를 가진 핸들이 확인된
  경우에만 프로그램/PID를 표시한다. 현재 앱이 확인되면 `[현재 앱]`도 표시한다.
- 조회 결과는 실패 직후의 관측값이다. 프로그램은 그 사이 종료하거나 포트를 닫을 수 있다.
- 보호된 프로세스, 권한, 드라이버 또는 조회 한계로 프로그램을 찾지 못하면 그 한계를 안내한다.
  프로그램을 찾지 못한 것은 포트가 비어 있다는 증거가 아니다.
- 관리자 권한 요청, 다른 프로그램 종료, 타 프로세스 원본 핸들 강제 닫기를 하지 않는다.
- 선택한 COM 포트를 추가로 열거나 설정을 변경하지 않는다. 현재 COM 이름의 NT 장치 매핑과
  기존 핸들의 이름을 조회하며, 조회용 복제 핸들만 닫는다. 포트 오류 진단이 수신 콜백에서
  실행되거나 파일을 쓰는 경로는 없다.

## 시간·자원 제한과 배포

점유 조회는 `PortDiagnostics/SerialMonitor.PortDiagnosticWorker.exe`에서 수행한다.
정상 배포 시 보조 EXE, DLL, deps.json, runtimeconfig.json 및 SerialMonitor.Core.dll이
같은 하위 폴더에 포함된다. 보조 파일이 없거나 응답 형식이 맞지 않아도 원본 연결 오류를 유지한다.
앱을 `SelfContained=true`로 publish하면 보조 프로그램도 Windows x64 자체 실행형으로
publish하여 해당 폴더에 .NET 런타임을 함께 넣는다. 일반 개발 빌드는 설치된 .NET 8을 사용한다.

- UI 스레드에서 프로세스 시작/네이티브 조회를 수행하지 않는다.
- 앱 응답 제한은 2.5초이고 보조 프로세스의 자체 종료 타이머는 2초다.
- 네이티브 조회 루프는 1.4초/프로세스 1024개/핸들 100,000개로 제한한다.
- 시스템 전체 핸들 목록을 만들지 않는다. 요청한 앱을 먼저 확인하고 프로세스별로 순서대로
  조회한다. 각 스냅샷은 64 KiB에서 시작해 최대 8 MiB로 제한한다. 이 한도를 넘는 프로세스는
  추가 메모리를 할당하지 않고 건너뛰며, 다른 프로세스 조회는 계속한다. 확인된 점유자가 없고
  접근 권한·메모리·조회 범위 제한이 있으면 `Limited`로 안내한다.
- stdout은 32,768자, stderr는 2,048자다.
- 앱 프로세스당 동시에 하나의 진단 보조 프로세스만 허용한다. 취소/시간 초과 시 앱이 시작한
  보조 프로세스만 종료한다. 드라이버 정리가 지연되면 해당 조회의 정리가 끝날 때까지 새 조회를
  만들지 않아 보조 프로세스가 누적되지 않는다.
- Windows x64 프로세스 핸들 스냅샷 API/레이아웃을 지원하지 않는 환경에서는 조회 불가로 안내한다.
  네이티브 호출은 이 보조 프로그램에 격리되어 있다.

참조: [GetFileType](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfiletype),
[DuplicateHandle](https://learn.microsoft.com/en-us/windows/win32/api/handleapi/nf-handleapi-duplicatehandle),
[NtQueryInformationProcess](https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntqueryinformationprocess),
[EnumProcesses](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocesses).
`ProcessHandleInformation`과 x64 레코드 레이아웃은 공개 [phnt 선언](https://github.com/winsiderss/phnt/blob/master/ntpsapi.h)을
확인해 직접 구현했으며, 해당 내부 정보 클래스가 지원되지 않으면 조회 불가로 처리한다.

## 회귀 검사

`ProcessHandleSnapshotTests`는 134 MiB를 요구하는 응답의 사전 거부, 반복 크기 증가 상한,
잘못된 네이티브 반환 길이/개수와 레코드 오프셋·버퍼 수명을 검증한다.
`PortOwnerProbeProcessTests`의 실제 점유 검사는 `SERIAL_MONITOR_TEST_COM0COM_PAIR0=1`에서만
실행하며 레지스트리로 `CNCA0`가 com0com 가상 포트인지 먼저 확인한다. 현재 앱과 별도 프로세스가
점유한 경우를 각각 검증하고, 조회 후 기존 연결과 점유 프로세스가 유지되는지도 확인한다.
