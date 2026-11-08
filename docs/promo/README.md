# Serial Monitor 20초 한국어 홍보영상

[MP4](../media/serial-monitor-promo-ko.mp4) · [README GIF](../media/serial-monitor-promo-ko.gif)

최신 공식 릴리스 **v1.6.4**의 실제 설정창에서 시퀀스·룰·플러그인 제어 부분만 발췌했다.
큰 한글 문구와 작은 화면으로 내용을 전달하며, README에서 소리 없이 볼 수 있도록 MP4와 GIF 모두 무음이다.
시퀀스는 설정 예시로, 촬영 중 실제 장치에 명령을 전송하지 않았다.

- MP4: 1920×1080, 30 fps, 20초, H.264.
- GIF: 960×540, 12 fps, 20초, 무한 반복.
- 편집 원본: `index.html`, `compositions/`, `assets/style.css`.
- 구성·검토: [스토리보드](STORYBOARD.md), [사양](SPEC.md), [검토 기록](REVIEW.md).

## 다시 렌더링하기

Node.js 22 이상, FFmpeg와 FFprobe가 PATH에 있어야 한다. 저장소 루트에서 실행한다.
HyperFrames CLI 버전은 0.9.23으로 고정되어 있으며 첫 실행에 npm에서 내려받는다.

```powershell
cd docs/promo
npm run check
npm run render
npm run gif
```

`npm run dev`로 타임라인 미리보기를 열 수 있다. 에이전트는 `npx --yes hyperframes@0.9.23 preview --background`를 사용한다.
화면·문구를 바꾸면 `npm run check`와 장면별 스냅샷 검토 후 다시 렌더링한다.
번들 글꼴은 영상에 사용한 글자만 포함하므로 문구에 새 한글을 추가할 때 글꼴 서브셋도 갱신한다.
GIF 변환기에 별도 FFmpeg 경로를 주려면 `FFMPEG_PATH`를 설정한다.

## 자산 출처

- 앱 화면: [공식 v1.6.4 포터블](https://github.com/Establers/custom-serial-monitor/releases/tag/v1.6.4).
  ZIP SHA-256: `013c38d8863375f01cf75ae39501932298f09a082354b615b8d85ade7e2f528b`.
  촬영 도구의 동명 앱 구분을 위해 실행 파일 사본의 이름만 변경했으며 배포본 EXE 바이트와 DLL은 수정하지 않았다.
- 시퀀스 설정 예시: [촬영용 JSON](demo-sequence.json). 패턴과 명령은 실제 장치 프로토콜에 맞게 교체해야 한다. 앱에 가져온 임시 촬영 항목은 캡처 후 제거했다.
- 앱 아이콘: 이 저장소의 `icon.PNG`.
- Noto Sans KR: [Google Fonts 원본](https://github.com/google/fonts/tree/main/ofl/notosanskr), [SIL OFL](assets/fonts/OFL.txt). 로컬 WOFF2 서브셋을 번들한다.
- GSAP 3.15.0: [npm 배포본](https://www.npmjs.com/package/gsap/v/3.15.0), [라이선스 안내](assets/vendor/GSAP-LICENSE.txt). 원본 저작권 헤더를 유지한다.
- HyperFrames: [heygen-com/hyperframes](https://github.com/heygen-com/hyperframes). 카탈로그의 browser-device-stage를 검토하고, 사용자 요청에 맞게 별도 가상 창 장식 없이 실제 화면 발췌만 사용했다.
