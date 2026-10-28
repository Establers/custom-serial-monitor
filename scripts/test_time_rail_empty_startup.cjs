// Regression for the terminal restore handshake used by the native startup host.
// A zero-chunk restore still requires Commit; live RX does not end that restore.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const results = [];
  try {
    for (const recovery of ['empty-commit', 'nonempty-settings-rebuild']) {
      const page = await browser.newPage({ viewport: { width: 720, height: 460 }, timezoneId: 'Asia/Seoul' });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.addInitScript(() => {
        window.startupMessages = [];
        window.chrome = window.chrome || {};
        window.chrome.webview = { postMessage: message => window.startupMessages.push(message) };
      });
      await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href,
        { waitUntil: 'domcontentloaded' });
      await page.evaluate(() => {
        const base = new Date(2026, 9, 28, 10, 0, 0).getTime();
        window.startupRows = Array.from({ length: 200 }, (_, index) =>
          `\x1b]777;${index + 1},${base + index * 1000}\x07payload-only packet ${index + 1}\r\n`).join('');
        // This is the faulty native host sequence: empty startup snapshot means
        // its chunk loop executes zero times, so it never sends Commit.
        window.serialMonitorBeginReplaceLog();
        window.serialMonitorAppendLog(window.startupRows, true, 1);
      });
      await page.waitForFunction(() => {
        const queue = window.serialMonitorGetAppendQueueState();
        return parsedLogLineIdCount === 200 && !queue.writing && !queue.queueLength;
      });
      await page.waitForTimeout(350);
      const rail = page.locator('#timeRail');
      const bounds = await rail.boundingBox();
      await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      const blocked = await page.evaluate(() => ({
        snapshot: window.serialMonitorGetNavigationSnapshot({ binCount: 0 }),
        parsedCount: parsedLogLineIdCount, replacing: navigationReplacing,
        rowIndexBusy: navigationRows.isBusy(), mode: document.getElementById('timeRail').dataset.mode,
        disabled: document.getElementById('timeRail').getAttribute('aria-disabled'),
        jumps: window.startupMessages.filter(message => message.type === 'navigationJump').length
      }));
      assert.equal(blocked.parsedCount, 200);
      assert.equal(blocked.snapshot.count, 200);
      assert.equal(blocked.rowIndexBusy, false, 'The row index itself has completed');
      assert.equal(blocked.replacing, true, 'The missing Commit leaves the native restore handshake open');
      assert.equal(blocked.snapshot.busy, true);
      assert.equal(blocked.disabled, 'true');
      assert.equal(blocked.jumps, 0);

      await page.evaluate(method => {
        if (method === 'nonempty-settings-rebuild') {
          // Changing a presentation setting rebuilds a nonempty snapshot, which
          // enters the host chunk loop and consequently sends its missing Commit.
          window.serialMonitorBeginReplaceLog();
          window.serialMonitorQueueReplaceChunk(window.startupRows);
        }
        window.serialMonitorCommitReplaceLog(false, 2);
      }, recovery);
      await page.waitForFunction(() => {
        const state = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
        return state.count === 200 && !state.busy && document.getElementById('timeRail').getAttribute('aria-disabled') === 'false';
      });
      await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height * 0.51);
      await page.waitForTimeout(50);
      const tooltip = await page.locator('#timeRailTooltip').innerText();
      await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height * 0.51);
      const healed = await page.evaluate(() => ({
        busy: window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).busy,
        replacing: navigationReplacing, disabled: document.getElementById('timeRail').getAttribute('aria-disabled'),
        jumps: window.startupMessages.filter(message => message.type === 'navigationJump').length
      }));
      assert.match(tooltip, /2026-10-28/);
      assert.equal(healed.busy, false);
      assert.equal(healed.replacing, false);
      assert.equal(healed.disabled, 'false');
      assert.equal(healed.jumps, 1);
      assert.deepEqual(errors, []);
      results.push({ recovery, blocked, healed, tooltip, errors });
      await page.close();
    }
    const artifact = path.resolve(__dirname, '../artifacts/time-rail-empty-startup-repro.json');
    fs.mkdirSync(path.dirname(artifact), { recursive: true });
    fs.writeFileSync(artifact, JSON.stringify(results, null, 2));
    console.log(JSON.stringify({ artifact, results }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
