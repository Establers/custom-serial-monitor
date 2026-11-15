// Requires Playwright via NODE_PATH. Uses an isolated headless Edge process.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    // Match WebView's virtual host so bundled fonts use the same origin.
    const assets = path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm');
    await page.route('https://serialmonitor.local/**', route => route.fulfill({
      path: path.join(assets, new URL(route.request().url()).pathname.slice(1))
    }));
    await page.route('https://serialmonitor-loading.local/log_restore.png', route => route.fulfill({
      path: path.resolve(assets, '../Loading/log_restore.png')
    }));
    await page.goto('https://serialmonitor.local/index.html');
    await page.waitForSelector('.xterm-screen');
    await page.waitForTimeout(500);
    const result = await page.evaluate(async () => {
      const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
      const drain = async () => {
        for (let i = 0; i < 2000; i++) {
          const queue = window.serialMonitorGetAppendQueueState();
          if (!queue.writing && !queue.queueLength) return;
          await delay(5);
        }
        throw new Error('Terminal append queue did not drain');
      };
      window.serialMonitorSetScrollback(100_000);
      for (let batch = 0; batch < 30; batch++) {
        const text = Array.from({ length: 1000 }, (_, i) => {
          const id = batch * 1000 + i + 1;
          return `\x1b]777;${id},${1700000000000 + id}\x07packet ${id} 한글\r\n`;
        }).join('');
        window.serialMonitorAppendLog(text, true, batch + 1);
        await drain();
      }
      compactScrollback.compact(Infinity, Infinity);
      let fits = 0;
      const originalFit = fitAddon.fit.bind(fitAddon);
      fitAddon.fit = () => { fits++; originalFit(); };
      const start = performance.now();
      for (let i = 0; i < 90; i++) {
        terminalElement.style.width = `${[900, 500, 350][i % 3]}px`;
        window.dispatchEvent(new Event('resize'));
        window.serialMonitorRequestFit();
        const id = 30001 + i;
        window.serialMonitorAppendLog(`\x1b]777;${id},${1700000000000 + id}\x07packet ${id} 한글\r\n`, true, 31 + i);
        await delay(20);
      }
      const fitsDuringDrag = fits;
      await drain();
      await delay(500);
      const buffer = terminal.buffer.active;
      return {
        fitsDuringDrag, fits, elapsedMs: Math.round(performance.now() - start),
        columns: terminal.cols, expectedColumns: fitAddon.proposeDimensions().cols,
        first: buffer.getLine(0).translateToString(true),
        last: buffer.getLine(buffer.length - 2).translateToString(true),
        queue: window.serialMonitorGetAppendQueueState()
      };
    });
    assert.equal(result.fitsDuringDrag, 0, 'Continuous docking sizes should not repeatedly reflow history');
    assert.equal(result.fits, 1, 'Only the settled viewport should be fitted');
    assert.equal(result.columns, result.expectedColumns);
    assert.equal(result.first, 'packet 1 한글');
    assert.equal(result.last, 'packet 30090 한글');
    assert.equal(result.queue.queueLength, 0);
    assert.equal(result.queue.writing, false);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify(result, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
