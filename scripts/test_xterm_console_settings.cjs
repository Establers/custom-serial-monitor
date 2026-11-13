// Exercise shipped xterm keys, local echo and RX rendering through the actual bridge.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 600 } });
    page.setDefaultTimeout(10000);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.evaluate(() => {
      window.messages = [];
      window.chrome.webview = { postMessage: message => window.messages.push(message) };
    });
    const settings = (enter = 'Cr', backspace = 'Del', receiveNewline = 'Preserve') => ({ enter, backspace, receiveNewline });
    const configure = async value => assert.equal(await page.evaluate(value => window.serialMonitorSetConsoleSettings(value), value), true);
    const takeInput = () => page.evaluate(() => window.messages.splice(0).filter(message => message.type === 'terminalInput').map(message => message.data).join(''));
    let session = 1, request = 100;
    const start = async (value, echo = false) => {
      await page.evaluate(({ session, value, echo }) => {
        window.serialMonitorSetDirectInput(false, 0);
        terminal.reset();
        window.serialMonitorSetDirectInput(true, session, echo, false, value);
      }, { session: ++session, value, echo });
      await takeInput();
    };
    const append = async text => {
      const id = ++request;
      assert.equal(await page.evaluate(({ text, session, id }) => window.serialMonitorAppendDirectOutput(text, session, false, id), { text, session, id }), true);
      await page.waitForFunction(id => window.messages.some(message => message.requestId === id && message.success), id);
    };
    const state = () => page.evaluate(() => ({
      x: terminal.buffer.active.cursorX, y: terminal.buffer.active.cursorY,
      rows: Array.from({ length: 4 }, (_, row) => terminal.buffer.active.getLine(row).translateToString(true))
    }));

    for (const [mode, expected] of [['Cr', '\r'], ['Lf', '\n'], ['CrLf', '\r\n']]) {
      await start(settings(mode), true);
      await page.keyboard.type('abc');
      await page.keyboard.press('Enter');
      assert.equal(await takeInput(), 'abc' + expected, `Enter ${mode} sends exactly the selected bytes`);
      await page.waitForFunction(() => terminal.buffer.active.cursorY === 1);
      assert.deepEqual(await state(), { x: 0, y: 1, rows: ['abc', '', '', ''] }, 'Each Enter echoes one visible newline');
    }
    await start(settings(), true);
    await page.keyboard.type('abc');
    await page.waitForFunction(() => terminal.buffer.active.cursorX === 3);
    await configure(settings('Lf', 'Bs'));
    await page.keyboard.press('Backspace');
    await page.keyboard.type('d');
    await page.waitForFunction(() => terminal.buffer.active.getLine(0).translateToString(true) === 'abd');
    assert.equal(await takeInput(), 'abc\bd', 'Changing Backspace live preserves both the input session and echo span');
    await configure(settings('CrLf', 'Del'));
    await page.keyboard.press('Backspace');
    assert.equal(await takeInput(), '\x7f');
    await page.keyboard.press('Control+h');
    await page.keyboard.press('Control+c');
    assert.equal(await takeInput(), '\b\x03', 'Control-key bytes are independent of the physical-key preference');

    await start(settings());
    await page.evaluate(() => {
      const textarea = terminal.textarea;
      textarea.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }));
      textarea.value = '한';
      textarea.dispatchEvent(new CompositionEvent('compositionupdate', { data: '한', bubbles: true }));
      for (const key of ['Enter', 'Backspace']) {
        textarea.dispatchEvent(new KeyboardEvent('keydown', { key, keyCode: 229, isComposing: true, bubbles: true, cancelable: true }));
        textarea.dispatchEvent(new KeyboardEvent('keydown', { key, keyCode: 229, bubbles: true, cancelable: true }));
      }
    });
    assert.equal(await takeInput(), '', 'IME composition Enter/Backspace cannot submit or edit the remote command');
    await page.evaluate(() => terminal.textarea.dispatchEvent(new CompositionEvent('compositionend', { data: '한', bubbles: true })));
    await page.waitForFunction(() => window.messages.some(message => message.type === 'terminalInput'));
    assert.equal(await takeInput(), '한', 'IME composition commits only its text');

    await start(settings('CrLf', 'Bs'));
    await page.evaluate(() => terminal.paste('one\ntwo\r\nthree\b\x7f'));
    assert.equal(await takeInput(), 'one\rtwo\rthree\b\x7f', 'Paste retains xterm normalization and bypasses physical-key mapping');
    await page.evaluate(session => window.serialMonitorSetDirectInputLocked(session, true), session);
    await page.keyboard.press('Enter');
    await page.keyboard.press('Backspace');
    assert.equal(await takeInput(), '', 'Sequence locking also blocks configured keys');

    for (const [mode, text, expected] of [
      ['Preserve', 'a\nb', { x: 2, y: 1, rows: ['a', ' b', '', ''] }],
      ['Lf', 'a\nb', { x: 1, y: 1, rows: ['a', 'b', '', ''] }],
      ['Cr', 'a\rb', { x: 1, y: 1, rows: ['a', 'b', '', ''] }],
      ['Auto', 'a\rb\nc', { x: 1, y: 2, rows: ['a', 'b', 'c', ''] }]
    ]) {
      await start(settings('Cr', 'Del', mode));
      await append(text);
      assert.deepEqual(await state(), expected, `Receive newline ${mode}`);
    }
    await start(settings('Cr', 'Del', 'Auto'));
    await append('a\r');
    assert.equal(await page.evaluate(session => window.serialMonitorAppendDirectOutput('stale', session - 1, false, 900), session), false);
    await append('\nb');
    assert.deepEqual(await state(), { x: 1, y: 1, rows: ['a', 'b', '', ''] }, 'Split CRLF is not doubled and stale RX does not change normalization state');
    await configure(settings('Cr', 'Del', 'Preserve'));
    await append('\rX');
    assert.equal((await state()).rows[1], 'X', 'Switching back to Original immediately restores CR redraw');

    await page.evaluate(() => window.serialMonitorSetDirectInput(false, 0));
    await configure(settings('Lf', 'Bs', 'Auto'));
    assert.equal(await page.evaluate(() => window.serialMonitorAppendLog('normal\r\nlogs', false, 901)), true);
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 901 && message.success));
    assert.deepEqual(await state(), { x: 4, y: 1, rows: ['normal', 'logs', '', ''] }, 'Console preferences leave normal log rendering unchanged');
    assert.deepEqual(errors, []);
    console.log('PASS: Enter/Backspace exact TX, live updates, local echo, shortcuts, paste, locks, RX modes, split CRLF, stale sessions and normal logs');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
