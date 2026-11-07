// Fixture text is exported by TerminalOutputBufferTests through the actual C#
// RX/decoder and log pipeline. Do not substitute terminal.write test strings.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const fixtures = JSON.parse(fs.readFileSync(process.env.SERIALMONITOR_TERMINAL_RX_FIXTURE, 'utf8'));
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 600 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.evaluate(() => {
      window.messages = [];
      window.chrome.webview = { postMessage: message => window.messages.push(message) };
    });
    let session = 10;
    let request = 100;
    for (const fixture of fixtures) {
      await page.evaluate(id => window.serialMonitorSetDirectInput(true, id), ++session);
      for (const batch of fixture.Batches) {
        const requestId = ++request;
        assert.equal(await page.evaluate(({ batch, session, requestId }) =>
          window.serialMonitorAppendDirectOutput(batch, session, true, requestId), { batch, session, requestId }), true);
        await page.waitForFunction(id => window.messages.some(message => message.requestId === id && message.success), requestId);
      }
      const state = await page.evaluate(count => ({
        // A BS-space-BS erasure leaves an explicit space cell. Compare visible
        // text while checking the exact cursor coordinates separately.
        rows: Array.from({ length: count }, (_, index) => terminal.buffer.active.getLine(index).translateToString(true).trimEnd()),
        x: terminal.buffer.active.cursorX, y: terminal.buffer.active.cursorY
      }), fixture.Rows.length);
      assert.deepEqual(state, { rows: fixture.Rows, x: fixture.X, y: fixture.Y }, fixture.Name);
      assert.equal(await page.evaluate(id => window.serialMonitorAppendDirectOutput('stale', id - 1, false, 9990), session), false);
      assert.equal(await page.evaluate(() => window.serialMonitorAppendLog('formatted log must stay hidden', false, 9991)), false);
      await page.evaluate(id => window.serialMonitorSetDirectInput(false, id), session);
      const requestId = ++request;
      assert.equal(await page.evaluate(({ text, requestId }) => window.serialMonitorAppendLog(text, false, requestId),
        { text: fixture.NormalLog, requestId }), true);
      await page.waitForFunction(id => window.messages.some(message => message.requestId === id && message.success), requestId);
      const normal = await page.evaluate(() => Array.from({ length: terminal.buffer.active.cursorY + 1 },
        (_, index) => terminal.buffer.active.getLine(index).translateToString(true)).join('\n'));
      assert.ok(normal.includes('OFF'), 'OFF restores the retained normal log snapshot');
      if (fixture.Name === 'backspace') assert.ok(normal.includes('\\x08'));
      if (fixture.Name === 'cursor') assert.ok(normal.includes('\\x1B'));
    }
    assert.deepEqual(errors, []);
    console.log(`PASS: ${fixtures.length} C# RX scenarios render deletion, split ANSI, CR redraw, history and async prompt restoration; OFF logs and stale-session fencing preserved`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
