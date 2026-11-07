# 센서 프로토콜 PDF → Virtual Modbus Device JSON 작성 요청

첨부한 센서 제조사 프로토콜 PDF를 읽고, Serial Monitor의 Virtual Modbus Device에서 불러올 **JSON 파일**을 만들어 주세요. 이 프로그램은 센서 역할인 Modbus RTU Server이며, 실제 제품(Client)의 RX 요청에 자동으로 TX 응답합니다.

## 반드시 지킬 사항

- PDF에 적힌 레지스터 맵을 사용하세요. 자료형·배율·주소 기준·워드 순서를 추측하지 마세요. 불명확한 항목은 JSON을 만들기 전에 질문하고, 확인되지 않은 항목을 임의로 추가하지 마세요.
- 센서 ID와 초기 측정값은 사용자에게 확인하세요. PDF의 공장 기본값을 쓰면 그 근거와 페이지를 별도로 설명하세요. 설명은 JSON 밖에 쓰세요.
- 출력은 아래 양식과 정확히 같은 camelCase 키를 쓰는 유효한 JSON입니다. 주석·마크다운 코드 펜스·여분의 키·중복 키를 JSON 파일에 넣지 마세요.
- 최상위는 `schemaVersion: 1`, `devices: [...]`입니다. 장치마다 `name`, `unitId`(1–247), `registers`가 필요합니다. 여러 센서도 한 파일에 넣을 수 있습니다. 이미 불러온 장치와 같은 ID로 파일을 추가하면 거절되므로 ID를 확인하세요.
- 각 항목의 필수 키는 `name`, `area`, `address`, `dataType`과 `value` 또는 `rawWords` 중 하나입니다.
- `area`: `holding`(FC03/06/16), `input`(FC04), `coil`(FC01/05/15), `discrete`(FC02). 영역이 다르면 같은 주소를 사용할 수 있습니다.
- `address`는 **실제 패킷의 0 기반 주소(0–65535)**를 십진 정수로 적습니다. 전통적 레퍼런스 40001/30001/00001/10001의 첫 항목은 각각 해당 영역의 주소 0입니다. PDF에 1 기반 번호가 있으면 1을 빼고, 이미 0 기반/프로토콜 주소라면 빼지 마세요. `0x0100`은 JSON에서 `256`으로 적으세요.
- 숫자 자료형: `uint16`, `int16`은 레지스터 1개, `uint32`, `int32`, `float32`는 연속 2개, `float64`는 연속 4개를 차지합니다. `coil`/`discrete`는 `bool`과 JSON `true`/`false`만 사용합니다. ASCII 문자열, 비트필드, int64, 제조사 전용 명령은 이 버전에서 지원하지 않습니다. 필요하면 16비트 raw 레지스터 항목들로 나누되, 동작을 추측하지 마세요.
- `value`는 **공학 단위의 값**입니다. `공학값 = 원시값 × scale + offset`. `scale` 기본 1, `offset` 기본 0. 예: 원시값 250 = 25.0°C라면 `value: 25.0`, `scale: 0.1`. 정수 자료형은 역변환한 원시값이 정수이면서 자료형 범위 안에 있어야 합니다. bool에는 배율·오프셋을 적용하지 마세요.
- 숫자는 JSON number로 작성하세요. 문자열 숫자, NaN, Infinity는 금지합니다. 배율의 절댓값은 1e-12–1e12, offset은 ±1e12입니다.
- `byteOrder`는 각 16비트 값 내부의 바이트 순서로 `bigEndian`(기본) 또는 `littleEndian`입니다. `wordOrder`는 다중 레지스터 값의 워드 순서로 `highFirst`(기본) 또는 `lowFirst`입니다. float32의 ABCD/CDAB/BADC/DCBA는 각각 bigEndian+highFirst / bigEndian+lowFirst / littleEndian+highFirst / littleEndian+lowFirst입니다.
- `rawWords`를 쓰면 `value`를 생략하세요. 실제 주소 증가 순서의 16비트 정수 배열이며, 개수는 자료형의 레지스터 수와 같아야 합니다. 이것은 이미 인코딩된 워드이므로 배율/바이트/워드 순서를 다시 적용하지 않습니다.
- 같은 장치/영역 안에서 주소가 겹치면 안 됩니다. 예: float32 주소 10은 10과 11을 차지하므로 주소 11에 다른 항목을 두지 마세요.
- `writable: false`는 제품의 쓰기를 금지합니다. 앱 화면에서는 읽기 전용 센서의 측정값도 바꿀 수 있습니다. input/discrete는 제품이 쓸 수 없습니다.
- 선택 키: `unit`(최대 32자), `description`(최대 1024자, PDF 페이지와 해석 근거 권장), `writable`(기본 true), `byteOrder`, `wordOrder`, `scale`, `offset`.
- 장치/항목 이름 최대 128자. 전체 최대 32장치/8192항목. JSON 파일·한 번의 추가·전체 저장 구성은 최대 16 MiB입니다. 앱은 향후 값/ID 변경을 저장할 공간도 미리 검사합니다. 비어 있는 장치 목록/레지스터 목록은 금지합니다.
- 제품이 한 번에 읽는 범위에 미정의 주소가 있으면 Illegal Data Address로 응답합니다. PDF에서 예약 레지스터를 0으로 읽을 수 있다고 명시한 경우에만 그 주소도 정의하세요.
- 표준 FC01/02/03/04/05/06/15/16을 지원합니다. 제조사 전용 명령, 특정 센서의 내부 상태 머신·측정 지연·물리적 특성은 자동 재현하지 않습니다.

## JSON 예제 (실제 센서 정보가 아니므로 PDF에 맞게 바꾸세요)

```json
{
  "schemaVersion": 1,
  "devices": [
    {
      "name": "Example temperature sensor",
      "unitId": 1,
      "registers": [
        { "name": "Temperature", "area": "holding", "address": 0, "dataType": "int16", "value": 25.0, "scale": 0.1, "unit": "°C", "writable": false },
        { "name": "Measurement", "area": "input", "address": 0, "dataType": "float32", "value": 12.5, "byteOrder": "bigEndian", "wordOrder": "lowFirst" },
        { "name": "Enable", "area": "coil", "address": 0, "dataType": "bool", "value": true }
      ]
    }
  ]
}
```

생성 후 각 항목의 PDF 페이지, 0 기반 주소 변환, 레지스터 점유 범위, 배율, 워드 순서를 설명하고 모순/겹침이 없는지 확인해 주세요. 사용자의 제품이 요청하는 센서 ID·FC·시작 주소·수량과 이 맵이 맞는지도 점검해 주세요. 기계 검증용 양식은 배포 폴더의 `docs/virtual_modbus_device.schema.json`입니다.
