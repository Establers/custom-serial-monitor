# STM32 HEX 요청 → Modbus RTU 응답 — 펌웨어 1.5.0

보드는 부팅 직후 **조용한 RTU 슬레이브(장치 ID 1)** 로 기다린다. HEX 요청에 바이너리 RTU 응답을 보낸다.
문자 `MODBUS NORMAL`을 보낼 필요가 없다. 부팅 메시지·HEARTBEAT·버튼 로그도 RTU 응답에 섞이지 않는다.
NUCLEO-F103RB의 USART2와 ST-LINK 가상 COM을 사용하며, 추가 배선은 필요 없다.

## GUI에서 순서대로

1. COM3, **115200 / 8N1 / 흐름 제어 없음**으로 연결한다.
2. **HEX 모드**를 선택한다. HEX 바이트 타임아웃은 우선 40ms를 사용한다.
3. Protocol 탭에서 Modbus RTU 분석을 켜고, 독립된 시험을 원하면 통계를 초기화한다.
4. 아래 요청을 한 행씩 전송한다. 각 행 끝의 CRC 두 바이트도 포함한다. CR/LF를 추가하지 않는다.
5. 각 응답을 확인한 뒤 다음 행을 보낸다. 수동 시험에서는 1초 정도 간격이면 충분하다.

| 시험 | HEX 요청 | 기대하는 HEX 응답 | Protocol 결과 |
| --- | --- | --- | --- |
| 정상 읽기 | `01 03 00 00 00 02 C4 0B` | `01 03 04 00 FA 00 64 DB E9` | FC03 응답, 값 250 / 100, CRC 정상 |
| 없는 주소 | `01 03 02 00 00 02 C5 B3` | `01 83 02 C0 F1` | 예외 02, CRC 정상 |
| 응답 CRC 오류 주입 | `01 03 01 00 00 02 C5 F7` | `01 03 04 00 FA 00 64 DB E8` | CRC 오류 1 |
| 정상 복구 | `01 03 00 00 00 02 C4 0B` | `01 03 04 00 FA 00 64 DB E9` | 다시 정상; 오류 주입은 다음 응답에 남지 않음 |

처음 세 행만 한 번씩 보내고 TX/RX 모두 분석하면 **총 6건, TX 3 / RX 3, CRC 정상 5 / CRC 오류 1, 예외 1**이다.
불완전 프레임과 분석 큐 누락 청크/바이트는 0이어야 한다. CRC 정상에는 예외 응답과 정상 요청도 포함된다.
RX만 보면 응답 3건, CRC 정상 2 / 오류 1이다. 화면 표시 필터와 누적 통계는 구분해서 확인한다.

잘못된 **요청 CRC**도 별도로 시험할 수 있다. `01 03 00 00 00 02 C4 0A`에는 응답하지 않는다.
이때 응답이 없는 것은 정해진 슬레이브 동작이다. 분석 큐 누락이나 불완전 **RX**로 집계할 근거가 되지는 않는다.
이어서 정상 요청을 보내면 정상 응답이 돌아와야 한다.

## 구현 범위

- FC03 Holding Registers / FC04 Input Registers, 0 기반 주소 0=250, 1=100. 수량 1~2이며 주소 범위 안이어야 한다.
- 수량 0 또는 126 이상: 예외 03. 지원하지 않는 함수: 예외 01. 없는 주소: 예외 02.
- 시험 전용 주소 **0x0100, 수량 2**: 위 값 두 개에 대해 응답 CRC만 고의로 손상한다. FC03/FC04 모두 적용한다.
- 방송 주소 0, 다른 장치 ID, 잘못된 요청 CRC에는 응답하지 않는다. 응답 형태의 함수 코드는 무시한다.
- 요청 최대 256바이트. 초과·손상 프레임 안의 정상처럼 보이는 부분을 별도 요청으로 실행하지 않는다.
- 5ms 수신 무음 후 요청을 조립한다. 1ms tick 기반 UART 시험 구현이므로 RTU t1.5/t3.5 정밀 타이밍이나 RS-485 전기·송수신 전환 적합성 검증용은 아니다.
- 응답은 원자적인 TX 큐 쓰기이며 큐가 차면 대기한다. 미전송 응답은 최대 1개다. 마스터는 한 요청의 응답/타임아웃 뒤에 다음 요청을 보낸다.

## 저장된 시퀀스

[GUI 가져오기 파일](sequences/modbus/02-rtu-server.gui.json)을 **시퀀스 관리 → JSON 불러오기**에서 선택한다.
목록의 **STM32 Modbus RTU - 정상·예외·CRC·복구 검사**를 선택하고 실행한다.
HEX 모드, Modbus RTU 분석 활성화 상태에서 약 4.2초 동안 한 번 실행한다.
펌웨어가 LAB 모드라면 보드를 리셋한 다음 실행한다.

정상 읽기 → 주소 예외 02 → 응답 CRC 오류 → 정상 복구 → FC04 입력 레지스터 → 수량 예외 03 순서다.
각 요청 뒤 700ms의 HEX RX Check가 붙으며, 단계 이름과 메모에 기대 결과가 표시된다.
통계를 초기화하고 전체 6개를 실행하면 **TX 6 / RX 6, CRC 정상 11 / 오류 1, 예외 2, 불완전 0, 분석 큐 누락 0**이다.
RX 바이트 합계는 44바이트이며, RX Check 6개 모두 PASS가 기대값이다.

[CLI 정의](sequences/modbus/02-rtu-server.cli.json)도 제공한다. GUI가 실행 중일 때 기존 이름을 확인하고 `sequence save`로 등록할 수 있다.
GUI 가져오기용은 `.gui.json`, CLI 등록용은 `.cli.json`이다. 한쪽을 등록했다면 같은 이름을 중복 등록하지 않는다.
CRC 오류 시험의 RX Check는 고의로 손상한 바이트의 수신을 검사한다. 이 단계의 RX Check는 PASS, Protocol 탭에서는 CRC 오류로 보여야 한다.

## 빌드와 자동 검사

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1 -BuildDirectory build-rtu-server
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-modbus.ps1 -Mode Offline -BuildDirectory build-rtu-server
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-rtu-server.ps1 -Mode Offline
```

실제 UART 검사는 COM3를 사용하는 GUI 연결을 닫고 실행한다. 보드를 기록하거나 GUI를 조작하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-rtu-server.ps1 -Mode Live -Port COM3
```

17개 정상/오류/무응답/복구 사례의 요청·응답 원본을 저장하고, 독립적인 PC CRC 계산 및 앱 분석기 집계와 대조한다.
이 검사는 RX 바이트를 앱 분석기에 재생하며, 화면 렌더링을 직접 검증하지 않는다.
[실제 업로드·UART 결과](RTU-SERVER-VALIDATION.md)를 참고한다.

## 기존 텍스트 시험 기능

`LAB` + CRLF로 들어가면 기존 PING/STREAM/RUN/MODBUS 고정 프레임 송신 기능을 사용할 수 있다.
HEX 화면에서 진입하려면 `4C 41 42 0D 0A`를 보낸다. `ID?`, `BUILD?`, `STOP`도 기존 검사기 호환을 위해 LAB 모드로 진입한다.
`RTU` + CRLF (`52 54 55 0D 0A`)는 `OK RTU`를 한 번 보내고 조용한 RTU 모드로 돌아간다. 리셋해도 RTU 모드다.
기존 HEARTBEAT/버튼 시험은 LAB 진입 후 `MONITOR ON`을 명시한다.
[이전 고정 프레임 시험 설명](MODBUS-FIXTURES.md), [1.4.0 검증 기록](MODBUS-RTU-VALIDATION.md)은 별도 기능·이미지의 기록이다.
