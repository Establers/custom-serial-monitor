# 1.2.0 후보 빌드 검증 — 2026-10-31

binary payload를 최대 1,024바이트, 전체 프레임 1,035바이트로 확장한 후보 이미지다. 빌드·PC 검증이 통과했으며,
**이 빌드의 보드 기록·읽기 검증과 UART 실기 36개도 통과했다.**
후속 실기와 백업·종료 상태는 [1.2.0 온보딩 결과](ONBOARDING-1.2.0.md)에 기록했다.
이전 HARDWARE-RESULTS / PLUGIN-MCP-RESULTS의 PASS는 해당 보고서에 적힌 이전 BIN에 대한 것이다.

| 항목 | 결과 |
| --- | --- |
| 버전 / 프로토콜 | 1.2.0 / SERIAL-LAB/1 |
| build ID | `D4089FC9E87FBE0C` |
| BIN | `build-candidate/serial-lab.bin`, 11,568바이트 |
| SHA-256 | `E17E629901839006FD7AEF477246E4E4C1F430253BE68953B5135B1C192C1247` |
| 도구 | ARM GCC 12.3.1, 기존 STM32CubeF1 SDK |
| Flash | 11,568 / 131,072바이트 (8.83%) |
| RAM 예약량 | 14,544 / 20,480바이트 (71.02%, 4KB stack·256B heap 여유 포함) |
| 최대 payload / frame | 1,024 / 1,035바이트 |
| RX / TX / 제어 응답 예약 | 2,048 / 4,096 / 1,536바이트 |
| C 프로토콜 | 114개 PASS |
| 독립적인 .NET 계약 판정 | 81개 PASS, binary 경계·오류 판정 32개 포함 |
| GUI/CLI 시퀀스 정의 | 실제 앱 importer/validator로 5개/2개 PASS |
| PC CRC/순번 판정·PowerShell 구문 | PASS |
| 모드 전환·baud/reset 검사 실행 제한 | 명시 옵션 없이는 장치 접근 전에 거부함을 확인 |
| PC 검사 단계의 장치 접근 / GUI 모드 변경 | 없음 / 0회 |
| 후속 UART 실기 / MCP 호출 / GUI 모드 변경 | 36개 PASS / 73회 / 0회 |

추가 기능은 BUILD?, CAPS?, SELFTEST와 RUN의 고정 데이터 9종이다.
RUN은 실행 ID·빌드 ID·길이·CRC·trailer를 모두 확인하며, 다른 job/fault가 활성화돼 있으면 거부한다.
BYTES, CP949, CONTROL처럼 NUL·제어 문자·비 UTF-8 바이트도 문자열 변환 전에 raw로 비교한다.
오래된 token, 다른 빌드 ID, payload 손상, 잘린 trailer를 판정기에 넣어 거부함을 확인했다.

큰 binary payload 0/1/255/256/257/511/512/513/1023/1024 각각의 전체 길이·LEN 상위 바이트·
순번·payload·CRC를 비교했다. 256바이트 블록마다 패턴이 달라 블록 순서 교환도 검출한다.
헤더·LEN·payload·CRC 분할 입력, 1KB 프레임 2개 연속 입력, 큰 프레임 CRC 손상,
1025 길이 거부 후 정상 복구, 큰 미완성 프레임의 timeout 후 복구도 확인했다.
PC codec도 1025를 거부한다. FixtureContractCheck의 MCP 모드에 같은 실기 절차를 추가했지만,
후속 온보딩에서는 이 절차로 실제 UART 실기 36개가 통과했다.

검사용 LEN은 16비트 little-endian이다. 10비트 최댓값 1023과 1KiB 크기 1024를 구분한다.
실제 제품의 10비트 packing/flags 규격을 구현했다는 의미는 아니다.

LONG은 기존 고정 job 버퍼에 직접 생성하도록 변경했다. GCC `.su`에서 command 함수의 정적 스택은
2,384바이트에서 848바이트로 줄었다. 빌드는 함수당 2,048바이트 초과 경고를 오류로 처리하며
`.su` 파일을 남긴다. 이 기준은 함수별 기준이고 라이브러리·호출 체인·인터럽트 전체의 최대 스택을
자동 증명한 결과는 아니다.
1KB payload에 대한 binary_reply 함수의 정적 스택은 1,056바이트로 해당 기준을 통과했다.

전체 영수증은 `build-candidate/validation.json`, 입력과 바이너리 식별 정보는 `manifest.json`,
원본 C 출력은 `fixture-*.bin`, 로그는 `native-check.txt`, `contract-check.txt`, `asset-check.txt`에 있다.
큰 binary 원본 응답은 `binary-*.bin`에도 남겼다.
validation은 `hardwareVerified=false`, `deviceOperations=none`, `guiModeChanges=0`으로 기록했다.
이는 PC 검사 단계의 영수증이다. 후속 `onboarding.json`에는 실제 기록과 `hardwareVerified=true`를 연결했다.

이전에 기록한 `build/serial-lab.bin`은 계속 보관돼 있으며 SHA-256은
`8AB5587A5FF6016724B35ED36F21CE402D2C077EBAE36AEA9E62D9DE69FDC986`이다.
1.1.0 후보는 `build-history/0ED9D07E9D840448/`와 [이전 후보 검증 기록](CANDIDATE-1.1.0-VALIDATION.md)에 보존했다.

재실행과 실제 장치 검증 절차는 [개발 가이드](../../docs/device-test-development-guide.md)를 참고한다.
통신 외 UI·파일 기록·72시간 내구·물리 배선 검사 영역은 [test-plan.json](test-plan.json)에 별도로 정의했다.
