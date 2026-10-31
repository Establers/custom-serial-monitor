// Regression coverage in an isolated headless Edge; NODE_PATH may point at the bundled runtime.
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
      window.shortcutMessages = [];
      window.chrome.webview = { postMessage: message => window.shortcutMessages.push(message) };
      window.reservedShortcuts = window.serialMonitorShortcuts.reservedKeys;
      window.serialMonitorSetCommandShortcuts(['F1', 'A', '7', 'Ctrl+1', 'Alt+S', ...window.reservedShortcuts], window.reservedShortcuts);
      terminal.focus();
    });
    for (const key of ['F1', 'a', '7']) await page.keyboard.press(key);
    let messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.deepEqual(messages.map(message => message.shortcut?.toUpperCase()), ['F1', 'A', '7']);

    await page.keyboard.down('F1');
    await page.keyboard.down('F1');
    await page.keyboard.up('F1');
    messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.equal(messages.length, 1, 'Holding a key must send only once');

    await page.keyboard.press('Shift+F1');
    await page.keyboard.press('Escape');
    await page.keyboard.press('b');
    assert.deepEqual(await page.evaluate(() => window.shortcutMessages.splice(0)), []);

    // Even stale profiles containing built-in shortcuts must leave those actions intact.
    await page.keyboard.press('F3');
    await page.keyboard.press('Shift+F3');
    await page.keyboard.press('F4');
    messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.deepEqual(messages.map(message => message.action), ['next', 'previous', 'previous']);

    for (const key of ['Enter', 'Tab', 'Space', 'ArrowLeft', 'Alt+c', 'Alt+w', 'Alt+r']) {
      await page.keyboard.press(key);
    }
    messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.ok(messages.every(message => message.type !== 'shortcut'), 'Reserved keys must never become TX commands');
    assert.equal(messages.filter(message => message.type === 'toggleAutoScroll').length, 1);
    await page.evaluate(() => terminal.focus());

    await page.keyboard.press('Control+1');
    await page.keyboard.press('Alt+s');
    messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.deepEqual(messages.map(message => message.shortcut), ['Ctrl+1', 'Alt+S']);

    // Reproduce layout-specific key/code pairs through the real document and
    // xterm handlers without changing the machine's active keyboard layout.
    await page.evaluate(() => {
      window.serialMonitorSetCommandShortcuts(['1', '2', '6', 'A', 'F1', 'Ctrl+1'], window.reservedShortcuts);
      terminal.focus();
    });
    for (const [key, code, shortcut] of [
      ['&', 'Digit1', '1'], ['é', 'Digit2', '2'], ['-', 'Digit6', '6'],
      ['+', 'Digit1', '1'], ['ě', 'Digit2', '2'],
      ['a', 'KeyQ', 'A'], ['1', 'Numpad1', '1']
    ]) {
      await page.locator('.xterm-helper-textarea').dispatchEvent('keydown', { key, code });
      messages = await page.evaluate(() => window.shortcutMessages.splice(0));
      assert.deepEqual(messages.map(message => [message.type, message.shortcut]), [['shortcut', shortcut]], `${key}: ${code}`);
    }
    for (const event of [
      { key: 'é', code: 'Digit2', isComposing: true },
      { key: 'Dead', code: 'Digit6' }, { key: 'Process', code: 'Digit1' },
      { key: 'End', code: 'Numpad1' },
      { key: '&', code: 'Digit1', shiftKey: true },
      { key: '-', code: 'Digit6', ctrlKey: true },
      { key: '@', code: 'Digit2', ctrlKey: true, altKey: true }
    ]) {
      await page.locator('.xterm-helper-textarea').dispatchEvent('keydown', event);
      assert.deepEqual(await page.evaluate(() => window.shortcutMessages.splice(0)), [], JSON.stringify(event));
    }

    await page.evaluate(() => window.serialMonitorSetCommandShortcuts(['F1', 'f1'], window.reservedShortcuts));
    await page.keyboard.press('F1');
    assert.deepEqual(await page.evaluate(() => window.shortcutMessages.splice(0)), [], 'Duplicate bindings must be inactive');
    await page.evaluate(() => window.serialMonitorSetCommandShortcuts(['F1', 'Ctrl+1']));
    await page.keyboard.press('F1');
    await page.keyboard.press('Control+1');
    assert.deepEqual(await page.evaluate(() => window.shortcutMessages.splice(0)), [], 'Missing native policy must disable bindings');

    // Simulate a future app shortcut. Updating its one definition must enable
    // the action AND block user assignment, even with an outdated native list.
    await page.evaluate(() => {
      const definitions = structuredClone(window.serialMonitorShortcutDefinitions);
      definitions.find(definition => definition.action === 'GoTo').gestures.push('F2');
      window.serialMonitorShortcuts = window.serialMonitorShortcuts.createCatalog(definitions);
      window.serialMonitorSetCommandShortcuts(['F2'], []);
      terminal.focus();
    });
    await page.keyboard.press('F2');
    messages = await page.evaluate(() => window.shortcutMessages.splice(0));
    assert.ok(messages.some(message => message.type === 'openLogNavigation'), 'The newly added action must work');
    assert.ok(messages.every(message => message.type !== 'shortcut'), 'The new action must be reserved automatically');

    const contextPage = await browser.newPage();
    contextPage.on('pageerror', error => errors.push(error.message));
    await contextPage.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/context/index.html')).href);
    await contextPage.evaluate(() => {
      window.copiedMessages = [];
      window.chrome.webview = { postMessage: message => window.copiedMessages.push(message) };
      window.setContextText('Context copy regression');
      window.getSelection().selectAllChildren(document.getElementById('contextText'));
    });
    await contextPage.keyboard.press('Control+c');
    assert.equal((await contextPage.evaluate(() => window.copiedMessages))[0].text, 'Context copy regression');
    await contextPage.close();
    assert.deepEqual(errors, []);
    console.log('PASS: unused keys, non-English number rows, IME/NumLock/modifier isolation, reserved built-ins, duplicate and stale bindings, search, Auto Scroll and repeat suppression');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
