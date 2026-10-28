// Cold-start browser regressions: data arrives before fonts and initial fits settle.
// No active application/profile/device is used. NODE_PATH can use the bundled runtime.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const results = [];
  try {
    for (const scenario of [
      { name: 'off-immediate-font-pending', prefix: false, transport: 'append', fontDelay: 450 },
      { name: 'off-empty-restore-then-live', prefix: false, transport: 'empty-restore', fontDelay: 450 },
      { name: 'off-staged-restore-with-live', prefix: false, transport: 'restore', fontDelay: 450 },
      { name: 'off-partial-first-packet', prefix: false, transport: 'partial', fontDelay: 450 },
      { name: 'off-early-width-reflow', prefix: false, transport: 'append', fontDelay: 450, reflow: true },
      { name: 'off-million-capacity', prefix: false, transport: 'append', fontDelay: 450, capacity: 1000000 },
      { name: 'on-immediate-control', prefix: true, transport: 'append', fontDelay: 450 },
      { name: 'on-delayed-control', prefix: true, transport: 'append', fontDelay: 0, dataDelay: 600 }
    ]) {
      const context = await browser.newContext({ viewport: { width: 720, height: 460 }, timezoneId: 'Asia/Seoul' });
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      if (scenario.fontDelay) await page.route(/\.woff2(?:\?|$)/, async route => {
        await new Promise(resolve => setTimeout(resolve, scenario.fontDelay));
        await route.continue();
      });
      await page.addInitScript(options => {
        window.coldMessages = [];
        window.chrome = window.chrome || {};
        window.chrome.webview = { postMessage: message => window.coldMessages.push(message) };
        window.coldStates = [];
        document.addEventListener('DOMContentLoaded', () => {
          window.coldBoot = { fonts: document.fonts.status, at: performance.now(), readyState: document.readyState };
          const sample = setInterval(() => {
            if (!window.serialMonitorGetNavigationSnapshot) return;
            const snapshot = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
            const rail = document.getElementById('timeRail');
            window.coldStates.push({ at: Math.round(performance.now()), count: snapshot.count,
              busy: snapshot.busy, startTime: snapshot.startTime,
              rawCount: parsedLogLineIdCount, sourceStart: navigationRows.sourceStart(),
              railMode: rail?.dataset.mode, railDisabled: rail?.getAttribute('aria-disabled') });
          }, 40);
          setTimeout(() => clearInterval(sample), 1800);
          const start = () => {
            const base = new Date(2026, 9, 28, 10, 0, 0).getTime();
            const line = id => {
              const seconds = String(id % 60).padStart(2, '0');
              const prefix = options.prefix ? `[2026-10-28 10:00:${seconds}.000] RX < ` : '';
              return `\x1b]777;${id},${base + id * 1000}\x07${prefix}packet ${id} temperature=25.3 status=OK\r\n`;
            };
            const block = (first, count) => Array.from({ length: count }, (_, i) => line(first + i)).join('');
            window.serialMonitorSetScrollback(options.capacity || 1000);
            window.serialMonitorSetFont('Consolas, "Cascadia Mono", Menlo, monospace', 13);
            window.serialMonitorSetHexSelectionHintEnabled(false);
            window.serialMonitorSetAutoScrollEnabled(true);
            if (options.transport === 'restore') {
              window.serialMonitorBeginReplaceLog();
              window.serialMonitorQueueReplaceChunk(block(1, 100));
              window.serialMonitorQueueReplaceChunk(block(101, 100));
              window.serialMonitorCommitReplaceLog(false, 1);
              window.serialMonitorAppendLog(block(201, 100), true, 2);
            } else if (options.transport === 'empty-restore') {
              window.serialMonitorBeginReplaceLog();
              window.serialMonitorCommitReplaceLog(false, 1);
              window.serialMonitorAppendLog(block(1, 300), true, 2);
            } else if (options.transport === 'partial') {
              window.serialMonitorAppendLog(line(1).replace(/\r\n$/, ''), true, 1);
              window.serialMonitorAppendLog('\r\n' + block(2, 299), true, 2);
            } else window.serialMonitorAppendLog(block(1, 300), true, 1);
            setTimeout(() => window.serialMonitorSetFont('"JetBrains Mono Bundled", Consolas, monospace', 12), 0);
            if (options.reflow) {
              setTimeout(() => terminal.resize(24, 15), 0);
              setTimeout(() => terminal.resize(80, 24), 15);
              setTimeout(() => window.serialMonitorFit(), 40);
            }
            window.coldStarted = true;
          };
          if (options.dataDelay) setTimeout(start, options.dataDelay);
          else start();
        }, { once: true });
      }, scenario);
      await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href,
        { waitUntil: 'domcontentloaded' });
      await page.waitForFunction(() => window.coldStarted === true);
      await page.waitForTimeout(1100);
      const state = await page.evaluate(() => {
        const snapshot = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
        return { boot: window.coldBoot, snapshot, mode: document.getElementById('timeRail')?.dataset.mode,
          disabled: document.getElementById('timeRail')?.getAttribute('aria-disabled'),
          rowsBusy: navigationRows.isBusy(), rawCount: parsedLogLineIdCount,
          sourceStart: navigationRows.sourceStart(), queue: window.serialMonitorGetAppendQueueState(),
          states: window.coldStates, messages: window.coldMessages.filter(message => /Error|Failed/.test(message.type)) };
      });
      const rail = page.locator('#timeRail');
      const bounds = await rail.boundingBox();
      await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      await page.waitForTimeout(50);
      const tooltip = await page.locator('#timeRailTooltip').innerText();
      const before = await page.evaluate(() => window.coldMessages.filter(message => message.type === 'navigationJump').length);
      await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      const after = await page.evaluate(() => window.coldMessages.filter(message => message.type === 'navigationJump').length);
      const passed = state.snapshot.count > 0 && state.snapshot.busy === false && state.disabled === 'false' &&
        state.mode === 'time' && /2026-10-28/.test(tooltip) && after === before + 1 && errors.length === 0 && state.messages.length === 0;
      results.push({ name: scenario.name, passed, boot: state.boot, count: state.snapshot.count,
        mode: state.mode, disabled: state.disabled, rowsBusy: state.rowsBusy, rawCount: state.rawCount,
        sourceStart: state.sourceStart, tooltip, jumps: after - before, errors, messages: state.messages,
        states: passed ? undefined : state.states });
      await context.close();
    }
    console.log(JSON.stringify(results, null, 2));
    assert.deepEqual(results.filter(result => !result.passed).map(result => result.name), [], 'Every cold start must initialize a usable rail');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
