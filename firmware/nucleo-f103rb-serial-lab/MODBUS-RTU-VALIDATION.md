# Modbus RTU UART 실기 — 2026-11-07

NUCLEO-F103RB의 기존 테스트 펌웨어를 1.4.0으로 확장하고 기존 USB/USART2 경로로 검증했다.
앱의 실제 `SerialService`와 `ModbusRtuAnalyzer`를 재사용한 개발용 검사기이며 GUI 화면은 조작하지 않았다.
검사기 의존성은 다른 미커밋 앱 변경을 제외한 `ce87b30` 기반으로 따로 빌드했다.

## 이미지와 기록

| 항목 | 확인 결과 |
| --- | --- |
| ST-LINK / 보드 | `066FFF485753667187145434` / NUCLEO-F103RB |
| MCU / Flash | 장치 ID `0x410`, 128KiB |
| 기록 전 BUILD | 1.3.0 / `ACFA6988DE234AFE` |
| 새 BUILD | **1.4.0 / `18A28C65C833D606`** |
| 새 이미지 | 14,548바이트, `0x08000000` |
| 새 BIN SHA-256 | `EA8EDB3DF3D7EFCF00E6C97A7FD6513A843DDC29DD21ED7B4F093AEAA939ED30` |
| 정적 RAM | 14,856 / 20,480바이트; 함수별 스택 2KiB 경고를 오류로 처리한 빌드 통과 |
| 이전 Flash 백업 | `backups/066FFF485753667187145434-20261107-185655-402.bin`, 131,072바이트 |
| 백업 SHA-256 | `410AA22DFE0B6D3DFFDF15AA8E928EF00EEA71308FA4FFD7C337BFA6089427D4` |
| 업로드 | 전체 Flash 백업 후 해당 이미지 기록, `Download verified successfully`, 리셋 |

원시 build manifest는 `build-modbus-rtu/manifest.json`이다. 이 이미지는 실제 보드 BUILD와 비교했다.
다른 checkout/빌드 입력에서 생성되는 build ID와 BIN 해시까지 동일하다고 주장하지 않는다.

## 검사 결과

- ARM GCC `-Wall -Wextra -Werror` 빌드 통과.
- native C 검사 **171개**, 기존 독립 fixture/binary 계약 검사 **81개** 통과.
- 새 C 출력 10종을 별도 PC 기대 바이트와 CRC로 비교하고 앱 분석기에 입력한 Offline 검사 통과.
- COM3 / 115200 / 8N1 / Handshake None / native idle timeout **40ms**로 실기.
- 정상·CRC 오류·예외·짧은 CRC 후보 충돌·중첩 payload·손상 payload·합쳐진 입력·절단·80ms 간격·255바이트 응답 **10종** 모두 바이트/집계 일치.
- CRC와 CORRUPT는 각각 **CRC 오류 1 / 정상 0**. 정상 내부 요청을 만들어내지 않음.
- GAP은 실제 수신 청크 시각 차이도 확인했고 **불완전 2 / CRC 정상·오류 0**으로 집계.
- 10,005개를 추가 수신한 90,045바이트 전체가 일치. **전체 10,005 / 보관 10,000 / 만료 5 /
  CRC 오류 0 / 분석 누락 청크·바이트 0**. 목록 Clear 뒤 전체 건수 유지.
- 종료 MCU 상태: **rx_drops=0 / uart_errors=0 / tx_rejected=0 / parse_errors=0**.
- 별도 COM4 GUI 연결은 변경하지 않음. COM3는 검사 종료 후 해제, MCU는 MONITOR OFF.

1차 전체 실기 원본: `results/modbus-rtu-20261107-1857/`.
재사용 시퀀스 파서/검사기 증거 해시를 포함한 최종 실기 원본은 `results/modbus-rtu-final-20261107-1903/`이다.
각 폴더의 `report.json`, 전체 `.bin`, `*-chunks.json`, `board-status.txt`를 함께 확인한다.
이 로컬 생성물과 Flash 백업은 git에서 제외한다.

CRC 오류는 명시적으로 주입한 것이다. 실제 전송 장애나 분석 큐 누락으로 바꿔 보고하지 않았다.
작은 예산의 분석 큐 거부는 Offline 검사에서 1청크/9바이트로 별도 검증했다.
GUI 표시·탭 전환·RS-485 전기적 조건·장시간 연속 운전은 이 실기의 검증 범위가 아니다.

재실행 방법은 [Modbus RTU 시험 안내](MODBUS-RTU.md)를 따른다.
