# 1.1.0 후보 빌드 검증 — 2026-10-31

새로운 테스트 계약과 개발 가이드용 후보 이미지다. 빌드·PC 검증이 통과했으며,
**이 후보를 보드에 기록하거나 UART로 검사한 결과는 아직 없다.**
이전 HARDWARE-RESULTS / PLUGIN-MCP-RESULTS의 PASS는 해당 보고서에 적힌 이전 BIN에 대한 것이다.

| 항목 | 결과 |
| --- | --- |
| 버전 / 프로토콜 | 1.1.0 / SERIAL-LAB/1 |
| build ID | `0ED9D07E9D840448` |
| BIN | `build-history/0ED9D07E9D840448/serial-lab.bin`, 11,564바이트 |
| SHA-256 | `9649C56FEB9FD65B49028DD3C0219636F7583A9B5EDA2FC9FEAE70C47D2C0B12` |
| 도구 | ARM GCC 12.3.1, 기존 STM32CubeF1 SDK |
| Flash | 11,564 / 131,072바이트 (8.82%) |
| RAM 예약량 | 13,776 / 20,480바이트 (67.27%, 4KB stack·256B heap 여유 포함) |
| C 프로토콜 | 89개 PASS |
| 독립적인 .NET 계약 판정 | 49개 PASS |
| GUI/CLI 시퀀스 정의 | 실제 앱 importer/validator로 5개/2개 PASS |
| PC CRC/순번 판정·PowerShell 구문 | PASS |
| 모드 전환·baud/reset 검사 실행 제한 | 명시 옵션 없이는 장치 접근 전에 거부함을 확인 |
| 이번 실행의 장치 접근 / GUI 모드 변경 | 없음 / 0회 |

추가 기능은 BUILD?, CAPS?, SELFTEST와 RUN의 고정 데이터 9종이다.
RUN은 실행 ID·빌드 ID·길이·CRC·trailer를 모두 확인하며, 다른 job/fault가 활성화돼 있으면 거부한다.
BYTES, CP949, CONTROL처럼 NUL·제어 문자·비 UTF-8 바이트도 문자열 변환 전에 raw로 비교한다.
오래된 token, 다른 빌드 ID, payload 손상, 잘린 trailer를 판정기에 넣어 거부함을 확인했다.

LONG은 기존 고정 job 버퍼에 직접 생성하도록 변경했다. GCC `.su`에서 command 함수의 정적 스택은
2,384바이트에서 848바이트로 줄었다. 빌드는 함수당 2,048바이트 초과 경고를 오류로 처리하며
`.su` 파일을 남긴다. 이 기준은 함수별 기준이고 라이브러리·호출 체인·인터럽트 전체의 최대 스택을
자동 증명한 결과는 아니다.

전체 영수증은 `build-history/0ED9D07E9D840448/validation.json`, 입력과 바이너리 식별 정보는 `manifest.json`,
원본 C 출력은 `fixture-*.bin`, 로그는 `native-check.txt`, `contract-check.txt`, `asset-check.txt`에 있다.
validation은 `hardwareVerified=false`, `deviceOperations=none`, `guiModeChanges=0`으로 기록했다.

이전에 기록한 `build/serial-lab.bin`은 계속 보관돼 있으며 SHA-256은
`8AB5587A5FF6016724B35ED36F21CE402D2C077EBAE36AEA9E62D9DE69FDC986`이다.

재실행과 실제 장치 검증 절차는 [개발 가이드](../../docs/device-test-development-guide.md)를 참고한다.
통신 외 UI·파일 기록·72시간 내구·물리 배선 검사 영역은 [test-plan.json](test-plan.json)에 별도로 정의했다.
