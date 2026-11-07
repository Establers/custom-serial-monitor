const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.evaluate(() => {
      window.messages = [];
      window.chrome.webview = { postMessage: message => window.messages.push(message) };
    });
    let session = 0, request = 1000;
    const settle = () => page.waitForFunction(() => {
      const state = window.serialMonitorGetAppendQueueState();
      return !state.writing && state.queueLength === 0;
    });
    const screen = () => page.evaluate(() => ({
      rows: Array.from({ length: 5 }, (_, i) => terminal.buffer.active.getLine(i)?.translateToString(true).trimEnd()),
      x: terminal.buffer.active.cursorX, y: terminal.buffer.active.cursorY
    }));
    async function start(columns = 80, prompt = 'device> ') {
      const requestId = ++request;
      await page.evaluate(({ columns, prompt, requestId }) => {
        window.serialMonitorSetDirectInput(false, 0);
        window.serialMonitorClear();
        terminal.resize(columns, 8);
        window.messages.length = 0;
        window.serialMonitorAppendLog('old log\r\n' + prompt, false, requestId);
      }, { columns, prompt, requestId });
      await page.waitForFunction(id => window.messages.some(message => message.requestId === id && message.success), requestId);
      await page.evaluate(id => window.serialMonitorSetDirectInput(true, id, true), ++session);
    }
    async function keys(...values) {
      for (const value of values) await page.keyboard.press(value);
      await settle();
    }
    async function text(value) { await page.keyboard.insertText(value); await settle(); }
    async function expect(row, x, y = 1) {
      const state = await screen();
      assert.equal(state.rows[0], 'old log');
      assert.equal(state.rows[y], row);
      assert.equal(state.x, x);
      assert.equal(state.y, y);
    }

    await start();
    await keys('Backspace', 'Backspace');
    await expect('device>', 8);
    await text('abc');
    await keys('Backspace', 'Backspace', 'Backspace', 'Backspace', 'Backspace');
    await expect('device>', 8);
    await text('abc');
    await keys('ArrowLeft');
    await text('Z');
    await expect('device> abZc', 11);
    await keys('Home', 'Backspace', 'Delete', 'End', 'ArrowLeft', 'Backspace');
    await expect('device> bc', 9);

    for (const value of ['한', 'e\u0301', '😀', '👨‍👩‍👧']) {
      await start();
      await text(value);
      await keys('Backspace');
      await text('a');
      await expect('device> a', 9);
    }
    await start();
    await text('e');
    await text('\u0301');
    await keys('Backspace');
    await text('a');
    await expect('device> a', 9);

    await start(12);
    await text('abcdefghij');
    await expect('efghij', 6, 2);
    await keys(...Array(11).fill('Backspace'));
    await expect('device>', 8);
    assert.equal((await screen()).rows[2], '');
    await text('abcdefghij');
    await keys(...Array(8).fill('ArrowLeft'));
    await text('Z');
    await expect('device> abZc', 11);
    assert.equal((await screen()).rows[2], 'defghij');

    await start(12);
    await text('ab한cd');
    await keys(...Array(6).fill('Backspace'));
    await expect('device>', 8);
    await start(12, 'device> abc');
    await text('한');
    await keys('Backspace');
    await expect('device> abc', 11);

    await start(12);
    await text('abcd');
    await keys('Control+c');
    const interrupted = await screen();
    assert.equal(interrupted.rows[1], 'device> abcd');
    assert.equal(interrupted.rows[2], '^C');
    assert.equal(interrupted.x, 0);
    assert.equal(interrupted.y, 3);

    await start();
    await text('abc');
    await keys('Enter', 'Backspace', 'Backspace');
    await expect('', 0, 2);
    const requestId = ++request;
    await page.evaluate(({ session, requestId }) => window.serialMonitorAppendDirectOutput('device> ', session, true, requestId), { session, requestId });
    await page.waitForFunction(id => window.messages.some(message => message.requestId === id && message.success), requestId);
    await keys('Backspace');
    await expect('device>', 8, 2);
    assert.equal((await screen()).rows[1], 'device> abc');
    assert.deepEqual(errors, []);
    assert.deepEqual(await page.evaluate(() => window.messages.filter(message => message.type === 'terminalInputEchoError')), []);
    console.log('PASS: prompt boundaries, excess Backspace, insertion/navigation/delete, wide and combining glyphs, wrapped editing and Enter/RX boundaries');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
