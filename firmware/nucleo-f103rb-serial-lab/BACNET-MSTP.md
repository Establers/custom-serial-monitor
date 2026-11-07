# BACnet MS/TP 프레임 시험 — 펌웨어 1.6.0

기존 Modbus RTU 장치 ID 1을 유지하면서 **MS/TP MAC 1의 Test Request/Response 시험 기능**을 추가한다.
펌웨어를 바꾸거나 Terminal 모드로 전환할 필요 없이 두 종류의 HEX 요청을 보낼 수 있다.
설정은 NUCLEO-F103RB / ST-LINK COM3 / **115200 / 8N1 / 흐름 제어 없음**이다.

## 앱에서 실행

1. 새 앱에서 COM3에 연결하고 **HEX 모드**로 설정한다. 바이트 타임아웃은 우선 40ms를 사용한다.
2. Protocol 탭에서 **BACnet MS/TP**를 선택한다. Modbus 시험으로 돌아갈 때는 **Modbus RTU**를 선택한다.
3. 시퀀스 관리 → JSON 불러오기에서 [MS/TP GUI 시퀀스](sequences/mstp/01-mstp-cases.gui.json)를 선택한다.
4. **STM32 BACnet MS/TP - 정상·CRC·복구 검사**를 한 번 실행한다. 약 2.8초, 요청 4개와 RX Check 4개다.
5. 정상 → 헤더 CRC 오류 → 데이터 CRC 오류 → 정상 복구의 원본 응답 검사 모두 PASS가 기대값이다.
   고의 CRC 오류 단계는 손상 바이트를 정확히 받으면 RX Check PASS이며, Protocol 통계에는 CRC 오류로 집계된다.

통계를 초기화하고 실행하면 **TX 4 / RX 4, CRC 정상 6 / 오류 2, 헤더 오류 1 / 데이터 오류 1,
불완전 0, 미해석 0, 분석 큐 누락 0**이다. 실제 RX 합계는 54바이트다.
분석 프로토콜을 바꾸면 이전 프로토콜의 선택·목록 일시정지·필터·통계가 새 결과에 섞이지 않도록 초기화한다.
같은 프로토콜에서 목록 지우기는 누적 통계를 보존하며, 통계 초기화 버튼은 별도다.

## 패킷 예시

장치 MAC 2가 MAC 1에 `ABC`를 보내는 표준 Test Request:

```text
55 FF 03 01 02 00 03 C8 41 42 43 2F 9F
```

| 바이트 | 의미 |
| --- | --- |
| `55 FF` | MS/TP 프리앰블 |
| `03` | Test Request |
| `01` / `02` | 목적지 MAC / 출발지 MAC |
| `00 03` | 데이터 길이 3, 상위 바이트 먼저 |
| `C8` | 앞의 종류·주소·길이에 대한 헤더 CRC |
| `41 42 43` | 시험 데이터 `ABC` |
| `2F 9F` | 데이터 CRC, 하위 바이트 먼저 |

정상 Test Response는 다음과 같다. 목적지/출발지가 바뀌고 응답 종류는 `04`다.

```text
55 FF 04 02 01 00 03 6C 41 42 43 2F 9F
```

시퀀스의 오류 요청도 정상 CRC의 Test Request다. 정확히 `CRCH` (`43 52 43 48`) 페이로드이면 응답 헤더 CRC만,
`CRCD` (`43 52 43 44`)이면 응답 데이터 CRC만 1비트 손상한다. **이 예약 값은 시험 펌웨어의 오류 주입 기능**이며 표준 BACnet 서비스가 아니다.
그 외 데이터는 그대로 정상 응답한다. 오류 주입 상태가 다음 요청에 남지 않는다.
잘못된 요청 CRC, 다른 목적지 MAC, 방송 주소, Test Request 이외의 프레임에는 응답하지 않는다.

## 분석 범위와 수신 경계

- 일반 MS/TP의 헤더 CRC와 데이터 CRC를 별도로 확인한다. 길이 0의 제어 프레임에는 데이터 CRC가 없다.
- Token / Poll For Master / Reply To Poll For Master / Test Request / Test Response / Data Expecting Reply / Data Not Expecting Reply / Reply Postponed를 구분한다.
- 일반 프레임 데이터 최대 501바이트, 전체 511바이트와 선택적인 마지막 `FF` 패딩을 처리한다.
- CRC가 정상인 헤더의 선언 길이로 조립하므로 USB 수신 청크를 프레임으로 오인하지 않는다.
  데이터 내부의 `55 FF`와 정상처럼 보이는 중첩 프레임도 바깥 프레임 CRC 오류를 숨기기 위한 재동기화 근거로 쓰지 않는다.
- 손상 헤더는 주소·프레임 종류·데이터 길이를 신뢰하지 않는다. 수신 무음 또는 다음 정상 헤더의 제한된 복구 근거를 기다리며,
  조립 메모리는 2KiB로 제한한다. 불확실한 구간은 한 오류/미해석 구간으로 남을 수 있다.
- 예약·업체 전용 종류와 미지원 COBS 확장 프레임은 미해석으로 표시한다. 확장 데이터 CRC를 일반 CRC로 계산해 거짓 정상/오류 판정을 하지 않는다.
- NPDU/APDU 서비스, ReadProperty 값, Who-Is/I-Am, 토큰 순환 관리와 장치 인증은 이번 범위에 포함되지 않는다.

이 펌웨어는 **분석기 검증용 격리 UART 시험 장치**다. 실제 공조 MS/TP 네트워크에 연결되는 토큰 관리 노드나 완전한 BACnet 제어기가 아니다.
현재 보드에는 RS-485 트랜시버와 DE/RE 제어가 없으며, 5ms 무음 기반 요청 조립은 버스 타이밍 적합성 검사가 아니다.
실제 버스 검증은 RS-485 하드웨어와 정상 토큰 관리 상대 장치로 별도 수행해야 한다.
CRC 정상은 링크 프레임 무결성의 확인이며 BACnet 서비스 성공을 뜻하지 않는다.
MS/TP에는 모든 프레임에 공통인 연속 순번이 없으므로, 관측만으로 물리적 누락 수를 확정하지 않는다.
앱 분석 큐에서 버린 청크/바이트와 불완전 조립·CRC 오류를 서로 구분한다.

## 자동 검사

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-build.ps1 -BuildDirectory build-bacnet-mstp
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-mstp.ps1 -Mode Offline
```

실제 UART 검사는 COM3 GUI 연결을 닫은 상태에서 실행한다. 자동 재전송이나 펌웨어 기록을 하지 않는다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-mstp.ps1 -Mode Live -Port COM3
```

[CLI 시퀀스](sequences/mstp/01-mstp-cases.cli.json)는 CLI 등록용이다. GUI RX Check 파일과 구분해서 사용한다.
[기존 Modbus 시험](MODBUS-RTU.md)과 기존 저장 시퀀스의 요청 바이트는 그대로 사용할 수 있다.
[업로드·실기·회귀 검증 기록](BACNET-MSTP-VALIDATION.md)에서 이미지 식별자와 실행 범위를 확인할 수 있다.

규격 대조 자료: [BACnet Stack의 프레임 구조](https://github.com/bacnet-stack/bacnet-stack/blob/master/src/bacnet/datalink/mstp.c),
[Annex G CRC 구현](https://github.com/bacnet-stack/bacnet-stack/blob/master/src/bacnet/datalink/crc.c),
[IETF MS/TP 개요와 표준 시험 프레임](https://www.rfc-editor.org/rfc/rfc8163.html#section-2.1).
CRC는 반사 다항식으로 직접 계산하며 외부 라이브러리나 테이블을 제품 코드에 복사하지 않는다.
