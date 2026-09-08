const assert = require('node:assert/strict');
const path = require('node:path');
const fs = require('node:fs');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
    const errors = [];
    await page.route('https://serialmonitor-loading.local/log_restore.png', route =>
      route.fulfill({ path: path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/Loading/log_restore.png') }));
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.evaluate(() => {
      window.serialMonitorShowRestoreOverlay('로그 화면 복원 중...', '1,000,000줄');
      window.serialMonitorUpdateRestoreProgress('1,000,000줄 · 42%\n경과 00:05 · 예상 남은 시간 ~00:07', 42);
    });
    assert.equal(await page.locator('#restoreOverlayProgress').evaluate(el => el.value), 42);
    assert.match(await page.locator('#restoreOverlayDetail').innerText(), /42%\n경과 00:05/);
    await page.locator('#restoreOverlay img').evaluate(img => img.decode());
    const output = path.resolve(__dirname, '../artifacts/restore-progress-preview.png');
    fs.mkdirSync(path.dirname(output), { recursive: true });
    await page.locator('#restoreOverlay .restore-card').screenshot({ path: output });
    await page.evaluate(() => {
      window.serialMonitorHideRestoreOverlay();
      window.serialMonitorShowRestoreOverlay('로그 화면 복원 중...', 'Preparing');
    });
    assert.equal(await page.locator('#restoreOverlayProgress').getAttribute('value'), null);
    await page.evaluate(() => window.serialMonitorUpdateRestoreProgress('계산 중', null));
    assert.equal(await page.locator('#restoreOverlayProgress').getAttribute('value'), null);
    await page.evaluate(() => window.serialMonitorUpdateRestoreProgress('완료', 100));
    assert.equal(await page.locator('#restoreOverlayProgress').evaluate(el => el.value), 100);
    await page.evaluate(() => window.serialMonitorHideRestoreOverlay());
    assert.equal(await page.evaluate(() => window.serialMonitorUpdateRestoreProgress('late update', 50)), false);
    assert.deepEqual(errors, []);
    console.log(`Progress overlay tests passed. Preview: ${output}`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
