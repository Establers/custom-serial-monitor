// Requires Playwright. Launches two separate Edge processes with disposable
// profiles, matching the application's isolated WebView2 environments.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

async function append(page, prefix, count) {
  await page.evaluate(({ prefix, count }) => {
    const text = Array.from({ length: count }, (_, i) =>
      `\x1b]777;${i + 1},${1700000000000 + i}\x07${prefix} row ${i}\r\n`).join('');
    window.serialMonitorAppendLog(text, false, 1);
  }, { prefix, count });
  await page.waitForFunction(() => {
    const state = window.serialMonitorGetAppendQueueState();
    return !state.writing && !state.queueLength;
  });
}

async function snapshot(page) {
  return page.evaluate(() => {
    const buffer = terminal.buffer.active;
    return {
      lines: Array.from({ length: buffer.length }, (_, i) => buffer.getLine(i).translateToString(true)),
      selection: terminal.getSelection(),
      viewportY: buffer.viewportY,
      background: terminal.options.theme.background,
      fontSize: terminal.options.fontSize,
      scrollback: terminal.options.scrollback,
      autoScrollEnabled
    };
  });
}

(async () => {
  const testRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'SerialInstanceBrowser_'));
  const contexts = [];
  const errors = [];
  try {
    for (const name of ['first', 'second']) {
      contexts.push(await chromium.launchPersistentContext(path.join(testRoot, name), {
        channel: 'msedge', headless: true, viewport: { width: 900, height: 600 }
      }));
    }
    const first = contexts[0].pages()[0];
    const second = contexts[1].pages()[0];
    for (const page of [first, second]) page.on('pageerror', error => errors.push(error.message));
    const url = pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href;
    await first.goto(`${url}?colorScheme=dark`);
    await second.goto(`${url}?colorScheme=light`);
    await append(first, 'first-only', 250);
    await append(second, 'second-only', 250);
    await first.evaluate(() => localStorage.setItem('instance-test', 'first'));
    assert.equal(await second.evaluate(() => localStorage.getItem('instance-test')), null);

    await second.evaluate(() => {
      window.serialMonitorSetFont('Consolas, monospace', 16);
      window.serialMonitorSetScrollback(2000);
      window.serialMonitorSetAutoScrollEnabled(false);
      terminal.scrollToTop();
      terminal.select(0, 2, 11);
    });
    const retained = await snapshot(second);
    assert.equal(retained.selection, 'second-only');
    assert.equal(retained.background, '#ffffff');

    await first.evaluate(() => {
      window.serialMonitorSetColorScheme('light');
      window.serialMonitorSetFont('Consolas, monospace', 10);
      window.serialMonitorSetScrollback(1000);
      window.serialMonitorSetAutoScrollEnabled(true);
      terminal.select(0, 0, 5);
      terminal.scrollToBottom();
      window.serialMonitorClear();
    });
    await append(first, 'first-after-clear', 50);
    assert.deepEqual(await snapshot(second), retained);
    await first.reload();
    assert.deepEqual(await snapshot(second), retained);
    await contexts[0].close();
    assert.deepEqual(await snapshot(second), retained);
    await append(second, 'second-after-first-exit', 1);
    const afterExit = await snapshot(second);
    assert.ok(afterExit.lines.some(line => line.includes('second-after-first-exit')));
    assert.ok(afterExit.lines.some(line => line.includes('second-only row 0')));
    assert.ok(afterExit.lines.every(line => !line.includes('first-only') && !line.includes('first-after-clear')));
    assert.equal(afterExit.background, retained.background);
    assert.equal(afterExit.fontSize, retained.fontSize);
    assert.deepEqual(errors, []);
    console.log('Instance isolation passed: separate browser profiles/processes, receive histories, clear, selection, scroll, font, capacity, theme, reload, and continued append after peer exit.');
  } finally {
    for (const context of contexts) await context.close();
    const fullRoot = path.resolve(testRoot);
    assert.equal(path.dirname(fullRoot), path.resolve(os.tmpdir()));
    assert.ok(path.basename(fullRoot).startsWith('SerialInstanceBrowser_'));
    fs.rmSync(fullRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
