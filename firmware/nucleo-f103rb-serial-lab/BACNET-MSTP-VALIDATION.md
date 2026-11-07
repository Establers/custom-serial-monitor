# BACnet MS/TP 추가 검증 — 2026-11-08

STM32 시험 펌웨어 1.6.0, 앱의 MS/TP 프레임 분석기, 정상·헤더 CRC·데이터 CRC·복구 시퀀스를 함께 검증했다.
기존 Modbus RTU 요청·응답과 등록된 시퀀스를 유지한다.

## 이미지와 실제 장치

- NUCLEO-F103RB / STM32F103RBT6 / device ID 0x410 / flash 128KiB
- ST-LINK SN: `066FFF485753667187145434`
- 업로드 BUILD ID: `D00570BE39B52177`, 버전 1.6.0, BIN 15,728바이트
- BIN SHA-256: `C7BD4E29328EF2239E00A86DD7870DC58974ACCADDB4130C5BA18B5E829B5605`
- 로컬 이미지/manifest: `build-bacnet-mstp/`
- 기록 전 전체 flash 백업: `backups/066FFF485753667187145434-20261108-000838-101.bin`, 131,072바이트
- 백업 SHA-256: `44A7C62F78D738675A9618714FA304A6F776F8ABAAEA835FD5854A2B34EBAD84`

대상 ID·용량을 확인하고 전체 flash 백업 후 다운로드·읽기 검증과 리셋을 완료했다.
옵션 바이트·보호 설정은 변경하지 않았다. FLASH 15,728 / 131,072바이트, RAM 15,904 / 20,480바이트이며
RAM 사용량에는 링크 스크립트의 stack/heap 예약이 포함된다.

## 소프트웨어 검증

- ARM GCC 12.3.1 빌드, 네이티브 C 검사 **753개**, 독립 PC 데이터 계약 검사 **81개** 통과.
- MS/TP 실제 C 출력 6종을 PC의 별도 테이블 CRC 계산과 앱 분석기로 대조해 통과.
- GUI v3 RX Check 및 CLI 정의의 파싱·원본 요청/응답 바이트·TX4/RX4 집계 계약 통과.
- BACnet/Modbus 관련 Core 검사 **51개** 통과. 전체 Core 검사 **577개** 통과.
- WinUI 전체 검사 **1,611개 통과 / 17개 건너뜀**. 장치 선택이 필요한 가상/물리 드라이버 시험은 자동 선택하지 않았다.
- 전체 솔루션 빌드 경고 0 / 오류 0. PowerShell 구문·WireCodec 검사 통과.
- 보관 한도 검사: 10,005개 분석 후 최근 10,000개 유지, 5개 퇴출, 누적 CRC 정상 10,005, 분석 큐 누락 0.
- 변경 전 HEAD `5a92c1f`의 별도 archive에 이번 작업 파일만 적용해 검증했다. 리소스 파일도 이번 키 5개씩만 반영했으며,
  검증한 파일과 Git stage의 해시를 대조했다. 다른 세션의 미완성 변경은 검증·커밋에 포함하지 않았다.
- 이 별도 checkout에서 만든 펌웨어 BUILD ID는 `F330516430A39E86`이다. 개행도 해시에 포함되므로 실제 기록 이미지와 구분한다.

전체 테스트 증거는 해당 별도 checkout의 `SerialMonitor.Core.Tests/TestResults/` 및
`SerialMonitor.WinUI.Tests/TestResults/` 아래 `bacnet-mstp-complete*.trx`에 있다.

## UART 실기

**COM3 / 115200 / 8N1 / 흐름 제어 없음 / native idle timeout 40ms**, 명령마다 650ms의 수신 창을 사용했다.
앱의 SerialService가 받은 실제 청크를 그대로 MS/TP 분석기에 전달했으며, 자동 재전송은 없다.

| 시험 | 실제 결과 |
| --- | --- |
| 정상 Test Request `ABC` | 정상 Test Response 13바이트, CRC 정상 |
| `CRCH` 시험 데이터 | 응답 14바이트, 헤더 CRC 오류 1 / 데이터 오류 0 |
| `CRCD` 시험 데이터 | 응답 14바이트, 헤더 CRC 오류 0 / 데이터 오류 1 |
| 정상 복구 | 다시 정상 13바이트 |
| 데이터 501바이트 | 정상 511바이트, 분할/오분할 없음 |
| 빈 시험 데이터 | 8바이트, 데이터 CRC 필드 없이 정상 |
| 손상 요청 CRC → 정상 요청 | 손상 요청 창에서 수신 0바이트, 다음 정상 응답 복구 |
| Modbus → MS/TP | Modbus 값 250/100 응답 후 MS/TP 정상 응답 유지 |

최종 MS/TP 증거: `results/mstp-final-20261108-001804-458/`.
기존 Modbus 실기 17종도 모두 통과: `results/modbus-after-mstp-20261108-001217-925/`.
두 검사 모두 MCU `rx_drops=0 uart_errors=0 tx_rejected=0`을 확인했다.
최종 분석기 Core DLL과 실행 후보의 SHA-256은
`4DD9F6A85B82D6F0AEC379BC69D74667D032DDE8C97D970591B04124518F84A8`로 일치한다.

## 검토에서 수정한 수신 경계

1. 큰 입력이 조립 버퍼를 채웠다는 이유만으로 끝에 남은 정상 프레임 조각을 종료하던 문제를 수정했다.
   300개 연속 프레임 뒤의 7바이트 조각과 10,005개 전체 프레임의 회귀로 확인한다.
2. 손상 헤더와 다음 정상 프레임 사이에 선택적 `FF` 패딩이 있으면 복구하지 못하던 문제를 수정했다.
   선언 길이 후보 바로 뒤의 패딩 1개와 정상 헤더 CRC가 함께 확인될 때만 복구한다.
   한 청크와 모든 청크 분할, 별도의 독립 재현에서 오류 1 / 정상 1로 복구됨을 확인했다.

초기 전체 검사에서는 CLI 동시 접속 시험의 연결 시간 초과가 한 번 발생했고, 별도 재시도에서는 테스트 실행 의존 DLL 누락으로 중단됐다.
해당 중간 실행을 통과로 계산하지 않았다. 솔루션 재빌드 후 최종 전체 검사 결과를 위에 기록했다.

## 등록과 범위

전체 검사 및 정상·헤더 CRC 오류·데이터 CRC 오류 단독 시퀀스 **4개를 기본 프로필에 등록**했다.
기존 18개를 보존해 총 22개이며, 각 시퀀스는 반복 1회와 RX Check를 포함한다.
프로필 원본은 `default.json.before-rtu-registration-20261108-001258-588-d69ce4d2.bak`로 백업하고 저장 후 재읽기 검증했다.

실행 후보는 저장소의 `artifacts/bacnet-mstp-app/SerialMonitor.WinUI.exe`다. 생성된 BIN·백업·검사 증거·앱 산출물은 Git에서 제외한다.
실행 후보의 시작 후 CLI 목록에서 22개 시퀀스와 MS/TP 4개 정의를 확인했다.
COM3 / 115200 / 8N1 / HEX / 40ms로 연결한 뒤 예시 Test Request를 한 번 보내
`55FF04020100036C4142432F9F` 13바이트를 받았다.
700ms 캡처 `eea5029d228f4026b7a7056929d77210`은 sendStatus=sent / captureStatus=completed,
누락 0 / serialErrors 0 / rxGapDetected=false / 미확인 페이지 없음이었다.
GUI 렌더링과 실제 RS-485 전기적 동작·토큰 네트워크·BACnet 애플리케이션 서비스 적합성은 이 검사의 범위 밖이다.
이 기능의 범위는 [사용 안내](BACNET-MSTP.md)의 격리 UART Test Request/Response 및 링크 프레임 분석이다.
