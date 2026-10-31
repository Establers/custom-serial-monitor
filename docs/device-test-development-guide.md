# 장치 펌웨어로 PC 도구를 검증하는 개발 가이드

장치를 반복 가능한 입력·출력 기준으로 만들고, PC 프로그램이 그 데이터를 정확히 처리했는지
독립적으로 판정한다. Serial Monitor와 NUCLEO-F103RB 구현을 예제로 삼되 다른 MCU·계측기·통신 도구에도
같은 개발 순서를 적용할 수 있도록 정리했다.

## 시작점

- 구현: [STM32 Serial Lab](../firmware/nucleo-f103rb-serial-lab/README.md)
- 기능별 검증표: [test-plan.json](../firmware/nucleo-f103rb-serial-lab/test-plan.json)
- MCU 명령 처리: [lab.c](../firmware/nucleo-f103rb-serial-lab/src/lab.c)
- 고정 데이터: [lab_fixtures.c](../firmware/nucleo-f103rb-serial-lab/src/lab_fixtures.c)
- 독립 판정 기준: [FixtureContract.cs](../firmware/nucleo-f103rb-serial-lab/tests/FixtureContractCheck/FixtureContract.cs)
- 실제 수신·검증기 결과를 구분한 이전 보고서: [하드웨어](../firmware/nucleo-f103rb-serial-lab/HARDWARE-RESULTS.md),
  [플러그인 MCP](../firmware/nucleo-f103rb-serial-lab/PLUGIN-MCP-RESULTS.md)

## 1. 기능마다 확인할 증거를 먼저 정한다

“정상 로그가 보인다”는 성공 기준으로 부족하다. 요구사항에 ID를 붙이고 입력, 예상 출력, 판정 코드,
실행 조건, 부작용을 연결한다. 현재 예제의 `test-plan.json`에는 22개 검증 영역이 있다.

| 범위 | 장치가 제공할 것 | PC에서 확인할 것 |
| --- | --- | --- |
| 송수신·인코딩 | 고정 문자열, 공백, UTF-8/CP949, 0x00..0xFF | 전체 바이트·길이·순서·CRC와 디코딩 |
| 경계·시간 | 긴 줄, 줄 끝, 바이트 분할, 지연·묵응답 | 수신 chunk와 무관한 처리, 관찰 창, 미일치 후 정책 |
| 부하·오류 | 번호 로그, burst, 누락·중복·CRC 주입 | 정확한 개수와 오류 위치, 큐·drop/error 카운터 |
| 이벤트·검색 | WARN/ERROR 앞뒤 문맥, 반복 텍스트·JSON | 예상 개수, 문맥, 필터·검색·강조·이동 |
| 기록·메모리 | 번호를 추적할 수 있는 지속 데이터 | 모든 회전 파일의 무결성, UI 보관 한도와 메모리 추이 |
| 자동화 | 동일한 명령 계약 | GUI·CLI·MCP별 실제 TX, 전체 RX, 실행·취소·재시작 |
| 물리 계층 | 별도 배선·장비 또는 사용자 조작 | USB 분리, RTS/CTS, RS-485, 전기적 오류 |

MCU가 테스트 데이터를 출력할 수 있다는 것과 PC의 모든 기능을 검증했다는 것을 구분한다.
예를 들어 `RUN EVENTS`는 규칙 검증용 데이터를 공급한다. 실제 이벤트 목록·알림·검색 결과는
GUI에서 별도의 기대값과 비교해야 한다. CRC 오류 주입도 실제 UART 전기적 잡음을 대신하지 않는다.

## 2. 변경할 코드와 검증할 코드를 분리한다

현재 예제는 다음 구조다.

```text
USART ISR → 고정 RX 큐 → main loop 명령 해석 → 고정 TX 큐 → USART ISR
                         └ 고정 데이터 / 시간 기반 job
PC verifier → 실제 수신 바이트 → 독립적인 예상 데이터와 비교 → 증거 파일
```

- ISR은 큐에 넣고 꺼내는 일만 한다. 명령 해석, printf 포맷, 디스크 쓰기, UI 작업을 넣지 않는다.
- PC에서도 수신·파싱·기록·이벤트·UI를 분리한다. UI는 묶어서 갱신하고 보관량을 제한한다.
- 송신 큐가 찼다고 로그를 무한히 저장하지 않는다. 생성 속도를 늦추거나 누락을 카운터로 드러낸다.
- 제어 응답용 공간을 따로 확보한다. 예제는 TX 4KB 중 1.5KB를 1KB-payload 응답·STOP·상태 응답용으로 남긴다.
- 시간 기반 job은 `poll(now)`로 처리한다. 가짜 시간으로 wrap, 지연, 취소, backpressure를 PC에서 검사할 수 있다.
- 타깃별 부분은 UART·clock·GPIO·reset·통계 인터페이스로 제한한다. 다른 보드는 이 계층과 시작 코드·링커부터 바꾼다.

## 3. 테스트 명령 계약을 만든다

필요한 기본 명령은 아래 네 종류다.

| 역할 | 예제 명령 | 계약 |
| --- | --- | --- |
| 식별 | `ID?`, `BUILD?`, `CAPS?` | 장치·프로토콜·펌웨어 빌드·지원 한도를 반환 |
| 진단 | `STATUS?`, `SELFTEST` | 수신 누락·UART 오류·제어 응답 거부·주입 오류를 구분 |
| 재현 | `RUN case token`, `STREAM`, `BURST`, `SPLIT`, `DELAY` | 입력이 같으면 예상 가능한 출력 |
| 정리·복구 | `STOP`, `FAULT CLEAR`, 명시적 reset | 취소 범위와 리셋되는 상태가 분명해야 함 |

명령의 대소문자, 줄 끝, 길이 상한, 잘못된 인자 처리, binary 길이·endian·CRC 범위를 문서에 적는다.
불완전한 패킷은 무한 대기하지 않고 제한된 시간·크기 안에서 복구한다.
입력 오류·주입 오류와 예상하지 못한 전송 손실은 다른 카운터로 기록한다.

현재 `SELFTEST`는 CRC 기준 벡터, 고정 데이터 생성, 내부 크기 상수를 검사한다.
SELFTEST PASS는 USB·UART·PC·GUI 경로를 통과했다는 뜻이 아니다. 전체 경로는 실제 RX로 별도 검증한다.

### 8비트를 넘는 길이와 1KiB 프레임

이 예제의 binary payload는 최대 1024바이트이며 전체 프레임은 1035바이트다.
LEN은 16비트 little-endian이다. 길이 255는 `FF 00`, 256은 `00 01`,
1023은 `FF 03`, 1024는 `00 04`로 표현한다. 10비트 길이값은 0..1023이고,
1024를 표현하려면 추가 비트 또는 길이-1 같은 별도의 규약이 필요하다.
실제 제품의 10비트 필드가 flags와 함께 packing돼 있다면 그 비트 배치·단위·endian을 별도로 구현한다.

현재 검사는 0/1/255/256/257/511/512/513/1023/1024 payload를 정확히 비교하고,
1025를 거부한다. 큰 payload는 256바이트 블록마다 패턴이 달라 블록 순서 교환도 잡는다.
LEN 중간에서 분할된 입력, 최대 프레임 2개를 합친 입력, 1KB 프레임의 CRC 손상,
미완성 큰 프레임의 timeout 후 복구도 검사한다. MCU C 출력과 PC의 독립 CRC·길이·payload를 비교한다.
기존의 43바이트 프레임 여러 개를 받는 시험과, 1035바이트 단일 프레임 시험을 구분해 기록한다.

## 4. 실행 ID와 빌드 ID로 오래된 응답을 배제한다

예제의 명령은 다음 형식이다.

```text
RUN UTF8 <이번 실행만의 token>
```

token은 영문·숫자·`_`·`-`로 1..32자다. 실제 검증기는 새 GUID를 사용한다.
응답은 아래처럼 길이를 가진 원본 바이트 메시지다.

```text
BEGIN run=<token> case=UTF8 len=<바이트 수> crc16=<4자리 HEX> build=<16자리 빌드 ID>\r\n
<정확히 len바이트의 payload>
\r\nEND run=<token> case=UTF8\r\n
```

검증 순서는 다음과 같다.

1. 선택한 manifest와 BIN SHA-256, 현재 소스 파일 해시가 맞는지 확인한다.
2. 실제 장치의 `BUILD?`가 manifest의 buildId·version과 정확히 일치하는지 확인한다.
3. token과 case를 비교해 이전 실행의 응답을 제외한다.
4. payload의 길이와 전체 바이트를 독립적인 기대값과 비교한다.
5. CRC와 END token까지 확인한다. 일부만 읽었으면 통과로 처리하지 않는다.

바이너리에 개행·NUL·`END`처럼 보이는 문자열이 들어갈 수 있으므로 개행이나 종료 문자열 검색만으로
payload 경계를 결정하지 않는다. 원본 바이트와 선언한 길이를 사용한다.
CRC는 CRC-16/CCITT-FALSE이며 RUN에서는 **payload만** 계산한다.
기존 바이너리 ECHO 프레임은 TYPE..PAYLOAD를 계산하므로 두 범위를 혼동하지 않는다.

buildId는 프로젝트 소스·빌드 스크립트·SDK 입력 해시·컴파일러 버전·컴파일 옵션에서 만든 식별자다.
BIN 자체의 SHA-256은 manifest에 별도로 기록한다. 이전 버전에서 얻은 PASS를 새 빌드의 결과로 옮기지 않는다.

## 5. 고정 데이터의 예상값을 독립적으로 만든다

예제에는 다음 9개가 있다.

| case | 재현 데이터 | 독립적인 판정 기준 |
| --- | --- | --- |
| TEXT | 연속 공백, 대소문자, 숫자, 기호 | ASCII 바이트와 공백·CRLF 전체 일치 |
| UTF8 | `안녕하세요 日本語 🚀` | PC 표준 UTF-8 인코더의 바이트 |
| CP949 | `안녕하세요` | PC 표준 CP949 인코더의 바이트 |
| BYTES | 0x00..0xFF | 0부터 255까지의 전체 순서와 길이 |
| NEWLINES | CRLF, LF, CR 혼합 | 실제 제어 바이트와 순서 |
| EVENTS | INFO → WARN → ERROR → INFO | 원본 줄 전체와 별도의 GUI 규칙 기대값 |
| JSON | 가상 sensor, seq, ok 필드 | 고정 JSON 원문. 실제 센서 측정값으로 해석하지 않음 |
| ANSI | 색상 시작·복구 | ESC 바이트와 텍스트. 화면 색상은 별도 관찰 |
| CONTROL | TAB, backspace, NUL | 문자열 길이 대신 원본 바이트 길이로 비교 |

MCU는 C의 명시적인 바이트 배열을 사용하고 PC 판정기는 표준 .NET 인코더와 별도의 기대값을 사용한다.
장치가 보내온 CRC가 맞는지만 검사하면 장치와 PC가 같은 잘못된 데이터를 받아들일 수 있다.
정답 payload 자체도 비교한다.

판정기에 일부러 틀린 token·빌드 ID·변경된 payload·잘린 trailer를 넣어 모두 거부하는지도 검사한다.
현재 offline 검사 49개는 이 음성 검사까지 포함한다.

RUN은 다른 job이나 오류 주입이 활성화돼 있으면 BUSY로 거부한다.
검증기가 남의 작업을 조용히 STOP하거나 fault 설정을 해제하지 않는다.

## 6. 빌드와 PC 검증을 한 명령으로 묶는다

이 PC에서 다음을 실행한다. 장치 연결·재기록·GUI 조작은 하지 않는다.

```powershell
Set-Location C:\Users\pjh\Desktop\serial\firmware\nucleo-f103rb-serial-lab
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1
```

기본 출력은 `build-candidate/`다. 기존에 기록한 `build/serial-lab.bin`과 이전 증거를 보존한다.

| 산출물 | 용도 |
| --- | --- |
| serial-lab.bin / .hex / .elf / .map | 기록 파일·주소·심벌·메모리 사용량 |
| *.su | GCC 함수별 정적 스택 사용량. 함수당 2KB 초과 경고는 오류로 처리 |
| manifest.json | 소스·SDK 입력·컴파일러·옵션·buildId·BIN SHA-256 |
| BUILD.txt / CAPS.txt / SELFTEST.txt / fixture-*.bin | 동일한 실제 C 코어를 PC에서 실행한 응답 |
| native-check.txt | C 명령·취소·오류·backpressure 검사 |
| contract-report.json | C 응답을 독립적인 PC 기대값과 비교한 결과 |
| validation.json | 빌드·C 프로토콜 검사·독립 판정·시퀀스 파서 결과의 영수증 |

`validation.json`은 명시적으로 `hardwareVerified=false`다.
ARM 컴파일과 native 테스트만으로 UART 실기나 GUI가 검증됐다고 기록하지 않는다.
다른 프로젝트에서는 고정된 검사 개수보다 그 빌드에서 실제 실행한 항목과 증거를 기준으로 판단한다.

## 7. 기록한 뒤에는 화면을 유지하는 MCP 검사부터 실행한다

장치 기록은 별도 단계다. 작업 중인 보드인지 확인하고 원래 Flash를 백업한 뒤 기록·읽기 검증한다.
이 PC의 명령 예제는 다음과 같다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\flash.ps1 `
  -SerialNumber 066FFF485753667187145434 -BuildDirectory build-candidate -ReplaceFirmware
```

GUI가 COM3 / 115200 / 8N1 / no handshake로 연결돼 있고 현재 job이 없을 때,
실제 플러그인 manifest를 통해 MCP 검사기를 실행한다.

플러그인이 구버전 설치 CLI를 선택하면 그 프로세스에만 호환 CLI를 지정할 수 있다.
이번 1.2.0 실기에서는 다음 경로의 CLI 1.6.2를 사용했다. 설치 파일이나 전역 설정을 바꾸는 명령은 아니다.

```powershell
$env:SERIAL_MONITOR_CLI = 'C:\Users\pjh\Desktop\serial\artifacts\serialctl\serialctl.exe'
```

```powershell
dotnet run --project .\tests\FixtureContractCheck -- mcp `
  C:\Users\pjh\Desktop\serial\plugins\serial-monitor `
  .\build-candidate\manifest.json `
  .\results\contract-live-새로운이름
```

이 검사기는 GUI 모드·baud·byte timeout·프로필을 변경하지 않는다.
`txMode=text/hex`와 `format=hex`를 요청마다 명시해 현재 화면 모드를 유지한다.
Terminal에서 binary 데이터가 보이는 모양은 원본 데이터와 다를 수 있으며, 판정은 raw RX로 수행한다.
다른 빌드가 올라가 있으면 BUILD? 단계에서 중단하므로 새 명령을 계속 보내지 않는다.
실행 실패가 곧 장치 실패라는 뜻은 아니다. transport gap·busy·불완전한 수집·빌드 불일치를 구분한다.

기존 `PluginMcpCheck`는 실제 Terminal↔HEX 전환 시험이므로 `--allow-mode-switch`를 요구한다.
`verify-advanced.ps1`도 속도 변경·모드 전환·reset을 수행해 `-AllowDisruptiveChecks`를 요구한다.
이 항목은 화면 로그 보존이 중요한 평상시 검사와 분리해 실행한다. 다른 과거 수동 검사기도 실행 전에
mode/clear/disconnect/reset 부작용을 확인한다.

## 8. 증거는 전체 수집과 연결 세대까지 보존한다

- 요청: 장치·포트·연결 세대·시각·token·정확한 command/txMode/줄 끝·관찰 창.
- 응답: sendStatus/captureStatus, capture ID, 전체 원본 RX, 페이지 offset/nextOffset, serial/drop/gap.
- 빌드: DUT BUILD?·호스트 manifest·BIN SHA-256·검증기 소스 해시·기능표 버전.
- 결과: 예상값과 비교한 항목, 실패 이유, 수행하지 않은 항목, 정리 후 상태.

MCP 미리보기는 전체 수집이 아니다. hasMore가 true이면 같은 capture ID와 format으로 nextOffset부터
끝까지 읽는다. 일부를 확인하고 남은 부분까지 통과했다고 쓰지 않는다.
sendStatus=sent 또는 unknown인 명령을 통신 오류만 보고 다시 보내지 않는다.
먼저 기존 결과와 raw RX를 확인한다. 명시적인 새 반복 시험이면 새 token으로 구분한다.

## 9. UI·디스크·장기 내구 검사는 별도 관찰값을 둔다

고정 데이터가 정상이라는 확인 뒤에 다음 검사를 수행한다.

1. 파일 기록을 켜고 번호 로그를 보내 모든 회전 파일의 순번 합계·중복·누락을 비교한다.
2. 보관 한도를 넘기면서 UI 줄 수와 프로세스 메모리·CPU·큐/drop/error를 시각과 함께 기록한다.
3. Pause 동안 전체 기록이 이어지는지 확인하고 Resume 후 표시 정책과 비교한다.
4. EVENTS/JSON/TEXT fixture의 예상 행 수와 GUI 이벤트·검색·필터·강조·이동 결과를 비교한다.
5. USB를 실제 분리해 연결 세대·재연결·진행 중 TX/시퀀스의 처리 결과를 확인한다.
6. 요구사항의 내구 시간을 실행하고 주기별 측정값·디스크 용량·오류·종료 상태를 남긴다.

현재 프로그램 요구사항은 72시간 내구다. 10초씩 여러 번 관찰하거나 몇 분 수신한 결과를
72시간 연속 통과로 바꾸어 쓰지 않는다. UI 메모리가 제한돼 있어도 기록 파일은 계속 증가하므로
시험 전 기록량과 보관 경로를 정한다.
정상 종료·취소·예상하지 못한 실패·아직 수행하지 않음을 구분해 표에 남긴다.

## 10. 다른 장치나 기능에 적용하는 절차

1. 새 요구사항에 ID를 붙이고 test-plan의 stimulus/oracle/validator/level/sideEffects를 작성한다.
2. 먼저 표준 문서·기존 정상 장치·독립 도구에서 정답 바이트나 측정 허용 범위를 확보한다.
3. MCU 생성 코드를 장치별 I/O와 분리해 작성하고 크기·큐·시간·취소 한도를 정한다.
4. 새 fixture를 추가하면 CAPS 목록, C export 테스트, PC 독립 예상값, test-plan을 함께 수정한다.
5. 잘못된 입력·경계 길이·취소·백압·시간 wrap과 판정기의 거짓 통과 방지 검사를 넣는다.
6. ARM 빌드, native 검증, 실제 통신, GUI/저장/장기 시험을 순서대로 올린다.
7. 빌드별 manifest와 실제 결과를 보존하고, 미실행 항목을 그대로 표시한 뒤 변경을 검토한다.

다른 MCU로 옮길 때는 메모리 크기·링커·시작 코드·클럭·UART 핀·IRQ·Flash 기록 방법을 해당 보드의
공식 자료로 다시 확인한다. F103RB의 PA2/PA3·20KB RAM·Flash 128KB 값을 그대로 복사하지 않는다.
배포 펌웨어에 진단 명령을 남길지, 별도 테스트 이미지로 분리할지도 제품 요구사항에 맞춰 정한다.

보드 참고: [ST Nucleo 매뉴얼 UM1724](https://www.st.com/resource/en/user_manual/um1724-stm32-nucleo64-boards-mb1136-stmicroelectronics.pdf).
