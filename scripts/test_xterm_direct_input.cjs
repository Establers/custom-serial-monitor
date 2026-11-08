// Runs the shipped terminal assets in an isolated headless Edge.
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
      terminal.focus();
    });
    const take = () => page.evaluate(() => window.messages.splice(0).filter(message => message.type !== 'terminalSearchChanged'));
    await page.keyboard.type('off');
    assert.deepEqual(await take(), [], 'Direct input defaults to OFF');
    await page.evaluate(() => window.serialMonitorAppendLog('existing log\r\n', true, 10));
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 10 && message.success));
    await take();
    assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInput(true, 0)), false);
    assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInput(true, 7, false)), true);
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(0).translateToString(true)),
      'existing log', 'Turning ON must preserve visible logs');

    await page.evaluate(() => window.serialMonitorSetCommandShortcuts(['A', '1', 'F1', 'Ctrl+1'], window.serialMonitorShortcuts.reservedKeys));
    await page.keyboard.type('cd ab/c');
    for (const key of ['Tab', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Backspace', 'Control+c', 'Enter'])
      await page.keyboard.press(key);
    let messages = await take();
    assert.ok(messages.every(message => message.type === 'terminalInput'), 'Keys must not trigger Auto Scroll or saved commands');
    assert.ok(messages.every(message => message.sessionId === 7));
    assert.equal(messages.map(message => message.data).join(''), 'cd ab/c\t\x1b[A\x1b[B\x1b[D\x1b[C\x7f\x03\r');
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(1).translateToString(true)), '', 'Device-echo mode has no local echo');

    await page.keyboard.type('a1');
    await page.keyboard.press('F1');
    messages = await take();
    assert.equal(messages.map(message => message.data).join(''), 'a1\x1bOP', 'Saved shortcut bindings must not eat direct input');
    assert.ok(messages.every(message => message.type === 'terminalInput'));

    await page.evaluate(() => {
      terminal.paste('two\nlines 한글 😀');
      window.serialMonitorAppendDirectOutput('device> cd ab/cd', 7, true, 700);
    });
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 700 && message.success));
    await page.evaluate(() => terminal.select(0, 1, 7));
    await page.waitForFunction(() => terminal.hasSelection());
    messages = await take();
    assert.equal(messages.filter(message => message.type === 'terminalInput').map(message => message.data).join(''),
      'two\rlines 한글 😀', 'Paste retains xterm newline handling and Unicode');
    await page.keyboard.press('Control+c');
    messages = await take();
    assert.equal(messages.length, 1);
    assert.equal(messages[0].type, 'copySelection', 'Copying selected text must not send Ctrl+C');

    await page.evaluate(() => terminal.clearSelection());
    await page.keyboard.press('Control+f');
    await page.keyboard.press('F3');
    messages = await take();
    assert.deepEqual(messages.map(message => message.type), ['searchShortcut', 'searchShortcut']);

    assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInput(false, 7)), true);
    await page.keyboard.type('zzz');
    assert.deepEqual(await take(), []);
    await page.keyboard.press('a');
    assert.equal((await take())[0].type, 'shortcut', 'OFF restores saved command shortcuts');
    await page.keyboard.press('Enter');
    assert.equal((await take())[0].type, 'toggleAutoScroll', 'OFF preserves the existing Enter shortcut');

    await page.evaluate(() => window.serialMonitorAppendLog('retained log\r\n', false, 20));
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 20 && message.success));
    await take();
    await page.evaluate(() => window.serialMonitorSetDirectInput(true, 8, true));
    await page.keyboard.type('new');
    await page.waitForFunction(() => terminal.buffer.active.getLine(1).translateToString(true) === 'new');
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(0).translateToString(true)), 'retained log');
    assert.equal(await page.evaluate(() => terminal.buffer.active.cursorY), 1, 'Typing must not submit a line');
    await page.keyboard.press('Backspace');
    await page.keyboard.type('1');
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => terminal.buffer.active.cursorY === 2);
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(1).translateToString(true).trimEnd()), 'ne1');
    assert.equal(await page.evaluate(() => terminal.buffer.active.cursorX), 0);
    messages = await take();
    assert.ok(messages.every(message => message.sessionId === 8));
    assert.equal(messages.map(message => message.data).join(''), 'new\x7f1\r', 'Only Enter adds the execution newline');

    await page.evaluate(() => window.serialMonitorSetDirectInputLocalEcho(false));
    await page.keyboard.type('remote');
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(2).translateToString(true)), '');
    await page.evaluate(() => window.serialMonitorAppendDirectOutput('remote', 8, false, 30));
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 30 && message.success));
    assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(2).translateToString(true)), 'remote', 'Device echo is not duplicated when local echo is OFF');
    await take();

    await page.evaluate(() => {
      const menuButton = document.createElement('button');
      menuButton.id = 'test-menu-button';
      document.body.append(menuButton);
      menuButton.focus();
    });
    assert.equal(await page.evaluate(() => window.serialMonitorFocusDirectInput(7)), false);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'test-menu-button');
    assert.equal(await page.evaluate(() => window.serialMonitorFocusDirectInput(8)), true);
    assert.equal(await page.evaluate(() => document.activeElement === terminal.textarea), true);
    await page.keyboard.type('z');
    assert.equal((await take()).map(message => message.data).join(''), 'z');
    assert.deepEqual(errors, []);
    console.log('PASS: preserved logs, immediate local echo, Enter-only newline, echo toggle, textarea focus, exact keys, copy/search and session changes');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
