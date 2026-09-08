// Requires Playwright (NODE_PATH may point at the bundled runtime packages).
// Uses an isolated, headless Edge instance; never touches a user's browser profile.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 850 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      if (message.type() === 'error' && /scrollback|append|replace/i.test(message.text())) errors.push(message.text());
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    const result = await page.evaluate(async () => {
      window.serialMonitorSetScrollback(100_000);
      for (let batch = 0; batch < 110; batch++) {
        const text = Array.from({ length: 1000 }, (_, i) => {
          const id = batch * 1000 + i;
          return `\x1b]777;${id + 1},${1700000000000 + id}\x07packet ${id} 한글 \x1b[31mRED\x1b[0m\r\n`;
        }).join('');
        window.serialMonitorAppendLog(text, true, batch + 1);
        while (window.serialMonitorGetAppendQueueState().writing || window.serialMonitorGetAppendQueueState().queueLength) {
          await new Promise(resolve => setTimeout(resolve, 5));
        }
      }
      terminal.scrollToTop();
      terminal.select(0, 0, 12);
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      const buffer = terminal.buffer.active;
      const first = buffer.getLine(0).translateToString(true);
      const last = buffer.getLine(buffer.length - 2).translateToString(true);
      const selection = terminal.getSelection();
      const lines = terminal._core._bufferService.buffers.normal.lines;
      let packed = 0;
      for (let i = 0; i < lines.length; i++) if (lines.get(i)._packedCells) packed++;
      return { first, last, selection, length: buffer.length, packed, visibleRows: terminal.rows };
    });
    assert.match(result.first, /^packet \d+ 한글 RED$/);
    assert.match(result.last, /^packet 109999 한글 RED$/);
    assert.match(result.selection, /^packet /);
    assert.equal(result.length, 100_000 + result.visibleRows);
    assert.ok(result.packed > 99_000);
    await page.setViewportSize({ width: 800, height: 600 });
    await page.waitForTimeout(500);
    await page.evaluate(() => { terminal.clearSelection(); terminal.scrollToBottom(); });
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ ...result, errors }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
