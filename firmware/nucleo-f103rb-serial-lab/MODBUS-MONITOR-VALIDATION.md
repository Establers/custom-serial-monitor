# 1.7.0 보드 기록·RTU 확장 실기 — 2026-11-13

이 기록은 아래 BIN에 대한 결과다. 이전 1.6.0 LAB·BACnet 기능을 유지하면서 RAM 직접 쓰기,
변하는 카운터, LD2 코일, 일회성 오류 주입을 추가했다. 화면 자동 조작은 사용하지 않았다.
주소표와 재실행 명령은 [MODBUS-MONITOR.md](MODBUS-MONITOR.md)에 있다.

## 이미지와 타깃

| 항목 | 실제 확인 |
| --- | --- |
| 보드 / MCU | NUCLEO-F103RB / STM32F103RB, device ID 0x410, Flash 128KiB |
| ST-LINK SN | `066FFF485753667187145434` |
| 포트 | ST-LINK VCP COM3, 115200 / 8N1 / handshake None |
| 펌웨어 / BUILD | `1.7.0` / `BD6B8BF1A4C200D7` |
| BIN 길이 / SHA256 | 19520 / `1E4E4E68DDA2089DEF006C5B75E840880CEA6BDBD2724B699D7709D10DDC6504` |
| ARM 빌드 | GCC 12.3, `-Wall -Wextra -Werror`, 함수별 스택 경고 2048B |
| 메모리 | Flash 19520B / 131072B, RAM 영역 16448B / 20480B 사용. RAM 수치는 링커의 4KiB stack + 256B heap 예약 포함 |
| 최대 보고 함수 스택 | 기존 `binary_reply` 1056B. 링커의 스택 예약 검사 통과 |

실제 초기 `BUILD?`는 1.6.0 / A4764BF8CC60569C였고 SELFTEST PASS를 확인했다.
`flash.ps1`은 지정한 probe와 MCU 용량을 대조한 뒤 기존 Flash 전체를 먼저 백업했다.

- 백업: [066FFF485753667187145434-20261113-213654-047.bin](backups/066FFF485753667187145434-20261113-213654-047.bin), 131072B.
- 백업 SHA256: `562582A0540D5BF4C884B2ED41F715D832762909D4679FB1949838F186C1B694`.
- 기록 주소 0x08000000. STM32CubeProgrammer의 `Download verified successfully`와 MCU reset을 확인했다.
- 이후 UART `BUILD?`를 위 manifest와 정확히 대조했다. RDP·옵션 바이트·ST-LINK 펌웨어는 변경하지 않았다.

## PC 검사

`scripts/check-build.ps1 -BuildDirectory build-modbus-monitor` 통과:

| 검사 | 결과 |
| --- | --- |
| strict MSVC C11 /W4 /WX | 5755 checks PASS |
| 독립 fixture 계약 | 81 checks PASS; 원본 C export와 길이·CRC·BUILD·CAPS·SELFTEST 대조 |
| 바이너리 경계 | payload 0/1/255/256/257/511/512/513/1023/1024; 최대 frame 1035B |
| 기존 Modbus fixture·분석기 | 10종 및 큐 제한, 이전 GUI/CLI 정의 파서 PASS |
| 이전 RTU 서버 | 9종 C 응답·분석기 및 TX3/RX3 통계 PASS |
| BACnet MS/TP | 6종 C 응답·분석기·GUI/CLI 정의 PASS |
| 확장 독립 oracle | FC01/02/03/04 최대 읽기, FC05/06/0F/10 최대 쓰기·응답 대조 PASS |
| 확장 GUI 정의 | 52단계 / TX26 / RX Check26 파서 및 HEX TX CRC PASS |

네이티브 검사는 잘못된 CRC/Unit/방송/범위/수량/길이/응답 용량에 쓰기 부작용이 없음을 확인한다.
최대 쓰기는 전체 검증 후 한 번에 반영하고, LED·쓰기 횟수는 값이 같아도 유효 요청당 한 번이다.
단일 응답 슬롯의 backpressure·지연·분할 전송 재개·새 요청 차단·LAB/STOP/CLEAR/RESET 취소,
tick 순환과 16비트 워드 경계의 카운터 carry도 검사했다.

## 실제 UART와 앱 서비스

실행 중인 GUI 인스턴스가 없음을 확인하고 직접 COM3를 열었다. 앱의 실제 `SerialService`와
`ModbusRtuMonitorService`를 사용했고 native byte timeout은 40ms였다.
테스트 판정은 앱의 request builder/decoder를 재사용한 값만으로 끝내지 않고 별도 CRC·패킷 oracle과 전체 TX/RX를 대조했다.

최종 자동 검사 24개가 모두 PASS했다. 아래 서비스 검사 22개에 이어 같은 GUI 정의 재생 2개를 포함한다:

- 실제 BUILD·ID, LAB PING·SELFTEST, 기존 Modbus 0/1, BACnet Test Request/Response.
- 부호·배율·UInt32·Float32·byte/word 교환 패턴.
- FC06/10 쓰기·readback, 123 words 쓰기 / 125 words 읽기.
- FC05/0F 코일·LED 상태, 1968 bits 쓰기 / 2000 bits 읽기, FC02/04 최대 읽기.
- 변하는 시간·쓰기·서버 읽기 카운터, 이번 읽기 포함, FC04 쓰기 거부 시 TX 없음.
- 폴링 응답 105회와 queued write 1회. 일반 TX와 실제 `CommandSequenceRunner`의 경쟁 송신 2건 차단.
  소유권 해제 뒤 일반 요청 성공. 실제 송신 바이트 수와 기록한 엔진 TX 합계 일치.
- DROP/CRC/TRUNCATE/WRONGID/SPLIT 80ms에서 오류 판정·값 무효화·원래 연결 종료·소유권 해제.
  알려진 fixture를 명시적으로 재연결·수집·초기화한 뒤 복구. DELAY 100ms는 timeout 1000ms 안에 성공.
- 쓰기 후 ACK DROP: TX 정확히 1회, 응답 없음, 재연결 후 값 0x55AA와 `writes=1` 확인.
  값 재조회 후에도 `writes=1`. 자동 재시도 없음.

최종 실기 증거: [report.json](results/monitor-complete-20261113/report.json),
[wire.jsonl](results/monitor-complete-20261113/wire.jsonl).
2026-11-13 21:51:20~21:52:39 KST, `passed=true`, `hardwareVerified=true`, 24 pass / 0 fail,
evidenceOverflow=false, serialErrors=[], 7646B / 537 chunks.
수신 시각은 호스트의 관찰 시각이며 물리 UART의 개별 바이트 시각을 측정한 것은 아니다.

## 같은 GUI 파일을 두 번 실제 실행

[03-monitor-extended.gui.json](sequences/modbus/03-monitor-extended.gui.json)의 정확한 파일을 가져와 사용했다.
SHA256은 `37DF6C4992588C68AFC7AB2B4968988D0442336479022F0896BF3C90C2AF0E63`이다.
실제 `CommandSequenceRunner`, 링크한 원본 `SequenceRunObserver.cs`, 실제 Core `SequenceRunSession`/RX matcher/store를 사용했다.
GUI 화면을 띄우는 대신 같은 실행·판정 경로를 실행하며, TX 인코딩도 실제 `TxPayloadEncoder`를 사용한다.

| 반복 실행 | 실제 TX / RX Check | 완료 / 불일치 / 불확실 | 소요시간 | 결과 |
| --- | --- | --- | --- | --- |
| 1 | 26 / 26 matched | 52 / 0 / 0 | 19.297초 | [개별 단계·종료 상태](results/monitor-complete-20261113/gui-round-1.json) |
| 2 | 26 / 26 matched | 52 / 0 / 0 | 19.104초 | [개별 단계·종료 상태](results/monitor-complete-20261113/gui-round-2.json) |

각 RX Check는 700ms의 전체 관찰 구간을 유지하며, 대기 합계는 회당 18.2초다.
각 단계의 실제 시작·종료·일치 시각과 Core 결과는 위 JSON 및 `sequence-runs/` 원본에 남겼다.
정의 파일의 TX26개와 별도로 종료 상태 확인용 ID?/RTU STATUS?/RTU 3개를 각 `-final-state` 라벨에 기록했다.
ID?는 RAM·카운터·오류 예약을 초기화하지 않는 escape이므로 상태를 먼저 지우지 않고 검사한다.
두 회 모두 `reads=0 writes=0 led=0 fault=NONE delay_ms=0 pending=0`, quiet RTU 복귀를 확인했다.

FC05/06/0F/10의 쓰기와 readback, 기존 읽기·새 입력 패턴·주소 예외·CRC 오류·정상 복구를 모두 순서대로 대조했다.
FC0F로 0x55를 쓴 뒤 FC05로 첫 코일만 끄면 0x54가 남는 것까지 검사한다.
FC02 GUI 패턴은 버튼과 CRC 바이트를 와일드카드로 허용한다. 별도 oracle은 전체 응답의 CRC·길이·미사용 비트·LED 관계를 계산해 판정했다.
각 회의 고의 CRC 오류 응답은 정확히 1개이며, 손상된 기대 바이트가 맞으므로 해당 RX Check도 matched다.
패킷 분석에서는 CRC 오류로 표시되는 것이 정상이다.

실제 GUI에서 재현하려면 같은 파일을 **시퀀스 관리 → JSON 불러오기**로 가져와 해당 이름을 선택하고 Run을 누른다.
자동 재연결 OFF, 폴링·브리지·다른 시퀀스 정지, HEX 모드가 조건이다. 파일 자체가 전역 재연결 정책을 바꾸지는 않는다.
Headless Live 검사는 실패 TX를 재시도하지 않으며, GUI의 전역 자동 재연결을 켜면 GUI는 연결 실패 TX를 재시도할 수 있다.

## 기존 기능 추가 회귀

| 실제 검사 | 결과 / 증거 |
| --- | --- |
| 이전 RTU 서버 | 17사례 PASS; 정상·예외·고의 CRC·방송/다른 Unit/잘못된 CRC 무응답·잘린/중첩/초과 요청 후 복구. [report](results/monitor-legacy-rtu-20261113-2137/report.json) |
| BACnet MS/TP | 6사례 PASS; 정상·header/data CRC·복구·501B 최대 payload·빈 payload. [report](results/monitor-legacy-mstp-20261113-2138/report.json) |
| LAB Modbus 송신기·분석기 | 10 fixture PASS. 10005프레임 90045B를 모두 대조, retained10000/evicted5, CRC 오류0, 분석 큐 누락0. [report](results/monitor-legacy-analysis-20261113-2141/report.json) |
| 다른 BUILD 차단 | 올바른 BIN hash에 다른 기대 BUILD ID를 지정. 최종 검사기에서 `BUILD?\r\n` 한 번만 송신하고 실패 종료. ID/LAB/RESET/쓰기를 보내지 않음. [실패 보고서](results/monitor-identity-final-20261113/report.json) |

FC06이 새로 지원되므로 기존 주소0 쓰기는 이제 주소 예외02다. 미지원 함수 검사는 0x41/예외01로 옮겼다.
기존 읽기 패킷과 GUI 01/02 시퀀스, LAB MODBUS fixture, MS/TP fixture 바이트는 유지했다.

마지막에는 별도 [상태 원본](results/monitor-final-state-complete-20261113.json)으로 BUILD·오류 카운터를 다시 확인하고
RAM/카운터 초기화, LED OFF, LAB/RTU 오류 해제, monitor OFF, quiet RTU로 두었다.
`parse_errors=0 rx_drops=0 uart_errors=0 tx_rejected=0`이며 COM3 연결은 닫아 사용자가 바로 연결할 수 있다.

주요 증거 SHA256:

| 파일 | SHA256 |
| --- | --- |
| 최종 report.json | `6B14DBAAED0FEFC013383EE11BBEDCBCC57EC86CD1F6BEF2757FA2F865EF866E` |
| 최종 wire.jsonl | `3560FD96D379FE9BB3945425A4686E8EA4A0A9BFBE04BC8C46DE66CA3ED2DBD1` |
| 최종 identity 실패 report | `9E5F7419C4246C7858BEEB2CF81DD0A0757A4393EAB98745C3B7998326CB3B95` |

최종 validator assembly SHA256 `889F1B4D697893C1B2169299FE465DEBD1E1EE5AD360EA096C215B015D0F3E94`;
Core assembly SHA256 `8B03A01AAAB8236AD2A2DE0C5FB61114D8E5F07EF7F1C1CF0DA3C5F7427EA07B`.

## 증거와 검증 범위

빌드·전체 native export·validator hash는 [build manifest](build-modbus-monitor/manifest.json)와
[PC validation](build-modbus-monitor/validation.json)에 있다. `validation.json`의 hardwareVerified=false는 PC 검사 자체의 범위다.
UART 증거의 hardwareVerified=true는 그 파일에 기록된 같은 BUILD/BIN에 대한 실제 통신을 뜻한다.
`build-*`, `backups`, `results`와 루트 `artifacts`는 로컬 증거 보관 폴더이며 Git에는 포함하지 않는다.

실제 GUI 컨트롤·표 렌더링·가져오기 대화상자 조작, 물리 USER 버튼 누르기·LED 광학 확인,
RS-485 전기 특성/DE 전환/정밀 t1.5/t3.5 적합성, 장시간 soak는 이번 PASS 범위에 포함하지 않는다.
USART2/ST-LINK VCP와 1ms tick / 5ms 요청 조립 무음을 사용하는 시험용 펌웨어다.
