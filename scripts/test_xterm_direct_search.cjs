const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const fixturePath = process.env.SERIALMONITOR_TERMINAL_SEARCH_FIXTURE;
  const replay = fixturePath && fs.existsSync(fixturePath + '.targets.json')
    ? JSON.parse(fs.readFileSync(fixturePath + '.targets.json', 'utf8')) : [];
  const cases = [
    { name: 'local-input-and-rx', term: 'STOP', expectedCount: 2, edit: true, raw: 'OK STOP\r\n' },
    { name: 'ansi-tab-cr', term: 'STOP', expectedCount: 2, raw: '\x1b[31mremoved\x1b[0m\r\x1b[2KSTOP\tSTOP\r\n' },
    { name: 'wrapped-unicode', term: 'STOP|한글|😀|e\u0301', regex: true, expectedCount: 5,
      raw: '한글 😀 e\u0301 STOP STOP\r\n' },
    { name: 'case-whole-word', term: 'stop', wholeWord: true, expectedCount: 3, raw: 'stop STOP STOPPED (Stop)\r\n' },
    { name: 'case-sensitive', term: 'STOP', wholeWord: true, matchCase: true, expectedCount: 1, raw: 'stop STOP STOPPED (Stop)\r\n' },
    { name: 'regex-overwrite', term: '\\d+', regex: true, expectedCount: 2, raw: 'id=7\r\x1b[2Kid=3 id=19\r\n' },
    { name: 'scrollback-duplicates', term: 'STOP', expectedCount: 3, raw: 'STOP\r\n' + 'line\r\n'.repeat(12) + 'STOP STOP\r\n' },
    { name: 'private-use-and-wide-wrap', term: '\uE000|한글', regex: true, expectedCount: 2, raw: '12345678901한글 \uE000  \r\n' },
    { name: 'no-matches', term: 'MISSING', expectedCount: 0, raw: 'STOP\r\n' },
    { name: 'alternate-screen', term: 'STOP', expectedCount: 1, raw: '\x1b[?1049hALT STOP\r\n' }
  ];
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const exported = [];
  let highlights = 0;
  try {
    for (const scenario of cases) {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
      await page.waitForSelector('.xterm-screen');
      await page.waitForTimeout(80);
      await page.evaluate(() => {
        window.messages = [];
        window.chrome.webview = { postMessage: message => window.messages.push(message) };
        terminal.resize(12, 8);
        window.serialMonitorAppendLog('old RX\r\n', false, 1);
      });
      const settle = () => page.waitForFunction(() => {
        const state = window.serialMonitorGetAppendQueueState();
        return !state.writing && state.queueLength === 0;
      });
      await page.waitForFunction(() => window.messages.some(m => m.requestId === 1 && m.success));
      await page.evaluate(() => window.serialMonitorSetDirectInput(true, 7, true));
      if (scenario.edit) {
        await page.keyboard.type('STX');
        await page.keyboard.press('Backspace');
        await page.keyboard.type('OP');
        await page.keyboard.press('Enter');
        await settle();
      }
      await page.evaluate(raw => window.serialMonitorAppendDirectOutput(raw, 7, false, 2), scenario.raw);
      await page.waitForFunction(() => window.messages.some(m => m.requestId === 2 && m.success));
      await settle();
      await page.evaluate(() => terminal.select(0, 0, 3));
      const snapshot = await page.evaluate(() => window.serialMonitorCaptureDirectSearch(7));
      assert.equal(snapshot.ok, true);
      assert.equal(await page.evaluate(() => terminal.hasSelection()), false, 'A new search must clear the previous highlight, including zero results');
      exported.push({ ...scenario, matchCase: !!scenario.matchCase, wholeWord: !!scenario.wholeWord,
        regex: !!scenario.regex, sessionId: 7, snapshot });
      for (const target of replay.find(item => item.name === scenario.name)?.targets || []) {
        const result = await page.evaluate(request => window.serialMonitorSearch(request), target);
        assert.equal(result.found, true, `${scenario.name}: exact host target ${JSON.stringify(target)}`);
        assert.equal(await page.evaluate(() => terminal.getSelection().replace(/\r?\n/g, '')),
          target.expectedText, `${scenario.name}: highlighted terminal cells`);
        highlights++;
      }
      if (scenario.name === 'scrollback-duplicates' && replay.length) {
        await page.evaluate(() => {
          terminal.options.scrollback = 5;
          window.serialMonitorAppendDirectOutput('new row\r\n'.repeat(40), 7, false, 4);
        });
        await page.waitForFunction(() => window.messages.some(m => m.requestId === 4 && m.success));
        const old = replay.find(item => item.name === scenario.name).targets[0];
        assert.equal((await page.evaluate(request => window.serialMonitorSearch(request), old)).found, false, 'Evicted markers must not select another duplicate');
      }
      if (scenario.name === 'case-sensitive' && replay.length) {
        const old = replay.find(item => item.name === scenario.name).targets[0];
        await page.evaluate(() => window.serialMonitorAppendDirectOutput('\x1b[2J\x1b[Hreplacement', 7, false, 5));
        await page.waitForFunction(() => window.messages.some(m => m.requestId === 5 && m.success));
        assert.equal((await page.evaluate(request => window.serialMonitorSearch(request), old)).found, false, 'Overwritten cells invalidate the original match');
        await page.evaluate(() => { window.serialMonitorSetDirectInput(false, 7); window.serialMonitorSetDirectInput(true, 8); });
        assert.equal((await page.evaluate(request => window.serialMonitorSearch(request), old)).found, false, 'Old ON sessions cannot select in a new session');
      }
      if (scenario.edit) {
        await page.evaluate(() => { terminal.clearSelection(); terminal.focus(); window.messages.length = 0; });
        const before = await page.evaluate(() => terminal.buffer.active.getLine(terminal.buffer.active.cursorY + terminal.buffer.active.baseY).translateToString(true));
        assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInputLocked(7, true)), true);
        await page.keyboard.type('BLOCKED');
        await page.keyboard.press('Enter');
        await page.evaluate(() => terminal.paste('BLOCKED PASTE'));
        await settle();
        assert.deepEqual(await page.evaluate(() => window.messages.filter(m => m.type === 'terminalInput')), []);
        assert.equal(await page.evaluate(() => terminal.buffer.active.getLine(terminal.buffer.active.cursorY + terminal.buffer.active.baseY).translateToString(true)), before);
        await page.evaluate(() => window.serialMonitorAppendDirectOutput('sequence RX\r\n', 7, false, 3));
        await page.waitForFunction(() => window.messages.some(m => m.requestId === 3 && m.success));
        assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInputLocked(6, false)), false);
        assert.equal(await page.evaluate(() => window.serialMonitorSetDirectInputLocked(7, false)), true);
        await page.keyboard.type('PING');
        await settle();
        assert.equal(await page.evaluate(() => window.messages.filter(m => m.type === 'terminalInput').map(m => m.data).join('')), 'PING');
        const updated = await page.evaluate(() => window.serialMonitorCaptureDirectSearch(7));
        assert.ok(updated.lines.some(line => line.text.includes('sequence RX')));
        assert.ok(updated.lines.some(line => line.text.includes('PING')));
        assert.ok(updated.lines.some(line => line.text.includes('STOP')), 'Lock/unlock must preserve ON screen history');
        const target = replay.find(item => item.name === scenario.name)?.targets[0];
        if (target) assert.equal((await page.evaluate(request => window.serialMonitorSearch(request), target)).found, false, 'An old snapshot must not select a duplicate');
      }
      assert.deepEqual(errors, []);
      await page.close();
    }
    if (fixturePath) fs.writeFileSync(fixturePath, JSON.stringify(exported));
    console.log(`PASS: ${cases.length} rendered snapshots, ${highlights} host highlight targets, input lock/paste rejection, RX while locked, same-session resume`);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
