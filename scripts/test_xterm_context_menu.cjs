// Isolated headless Edge; no native app, user browser profile or serial port.
// NODE_PATH may point to the bundled runtime's node_modules.
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
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    const result = await page.evaluate(async () => {
      const messages = [];
      window.chrome ??= {};
      window.chrome.webview = { postMessage(message) { messages.push(message); } };
      await new Promise(resolve => terminal.write('original 한글 selection\r\nnew selection\r\n', resolve));
      terminal.select(0, 0, 12);
      const original = terminal.getSelection();
      function openMenu() {
        document.querySelector('.xterm-screen').dispatchEvent(new MouseEvent('contextmenu', {
          bubbles: true, cancelable: true, clientX: 100, clientY: 100
        }));
      }
      function click(action) {
        messages.length = 0;
        document.querySelector(`#contextMenu button[data-action="${action}"]`).click();
        return messages.find(message => message.type === 'contextMenuAction');
      }
      const copied = [];
      for (const action of ['copy', 'searchSelected']) {
        terminal.select(0, 0, 12);
        openMenu();
        // Menu commands must use the original capture even if selection changes.
        terminal.select(0, 1, 3);
        copied.push(click(action));
      }
      terminal.clearSelection();
      openMenu();
      const disabledWithoutSelection = ['copy', 'searchSelected'].every(action =>
        document.querySelector(`#contextMenu button[data-action="${action}"]`).disabled);

      const largeSelection = 'AA '.repeat(1_000_000);
      const getSelection = terminal.getSelection;
      terminal.getSelection = () => largeSelection;
      const otherActions = [];
      for (const action of ['copyAllVisible', 'copySinceLastTx', 'copySinceLastMark', 'toggleAutoScroll', 'clear', 'quickMarker']) {
        openMenu();
        const message = click(action);
        otherActions.push({ action: message.action,
          hasSelectedText: Object.hasOwn(message, 'selectedText'), jsonChars: JSON.stringify(message).length });
      }
      terminal.getSelection = getSelection;
      return { original, copied, disabledWithoutSelection, otherActions, autoScrollEnabled };
    });
    assert.ok(result.original.includes('한글'));
    for (const [index, action] of ['copy', 'searchSelected'].entries()) {
      assert.equal(result.copied[index].action, action);
      assert.equal(result.copied[index].selectedText, result.original);
    }
    assert.equal(result.disabledWithoutSelection, true);
    for (const message of result.otherActions) {
      assert.equal(message.hasSelectedText, false, message.action);
      assert.ok(message.jsonChars < 200, message.action);
    }
    assert.equal(result.autoScrollEnabled, false);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ ...result, errors }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
