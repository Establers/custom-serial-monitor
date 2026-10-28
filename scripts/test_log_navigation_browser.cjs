// Isolated real xterm + Edge integration and million-line navigation checks.
// NODE_PATH may point to the bundled runtime's node_modules. --million adds 1M rows.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 700 }, timezoneId: 'Asia/Seoul' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.navigationMessages = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = { postMessage: message => window.navigationMessages.push(message) };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href + '?language=ko');
    await page.waitForSelector('.xterm-screen');
    await page.waitForTimeout(200);
    const result = await page.evaluate(async () => {
      const wait = async () => {
        for (let n = 0; n < 5000; n++) {
          const q = window.serialMonitorGetAppendQueueState();
          if (!q.writing && !q.queueLength && !navigationRows.isBusy()) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('Navigation did not settle');
      };
      const base = new Date(2026, 9, 28, 10, 0, 0).getTime();
      const line = (id, payload = `packet ${id}`) => `\x1b]777;${id},${base + id * 1000}\x07${payload}\r\n`;
      const currentText = index => terminal.buffer.active.getLine(navigationRows.rowAt(index))?.translateToString(true);
      window.serialMonitorSetScrollback(1000);
      window.serialMonitorClear();
      terminal.resize(80, 24);
      await wait();
      window.serialMonitorAppendLog(Array.from({ length: 500 }, (_, i) => line(i + 1)).join(''), true, 1);
      await wait();
      const initialTop = terminal.buffer.active.viewportY;
      const jump = window.serialMonitorNavigate('10:01:40');
      const targetText = currentText(jump.index);
      const targetViewport = terminal.buffer.active.viewportY;
      const back = window.serialMonitorNavigationBack();
      const restoredTop = terminal.buffer.active.viewportY;
      window.serialMonitorNavigate('#100');
      const relativeBaseIndex = navigationRows.currentIndex();
      const relative = window.serialMonitorNavigate('+30s');
      const relativeDifference = relative.index - relativeBaseIndex;
      const percent = window.serialMonitorNavigate('70%');
      const percentCount = navigationEngine.range().count;
      const pausedViewport = terminal.buffer.active.viewportY;
      window.serialMonitorAppendLog(line(501), true, 2);
      await wait();
      const viewportAfterRx = terminal.buffer.active.viewportY;
      const invalid = window.serialMonitorNavigate('#0');

      // Wrapped physical rows may evict far more logs than the metadata capacity.
      window.serialMonitorClear();
      terminal.resize(25, 18);
      await wait();
      window.serialMonitorAppendLog(Array.from({ length: 600 }, (_, i) => line(i + 1, `packet ${i + 1} ` + 'abcdefghij'.repeat(5))).join(''), false, 3);
      await wait();
      const retained = window.serialMonitorGetNavigationState();
      const oldest = window.serialMonitorNavigate('#1');
      const oldestRow = navigationRows.rowAt(0);
      const oldestText = currentText(0);
      const firstId = navigationEngine.resolve('#1', 0).id;
      const oldId = firstId;
      window.serialMonitorNavigate('80%');
      const sourceAnchor = captureNavigationAnchor();
      window.serialMonitorNavigate('#1');
      terminal.resize(42, 22);
      const busyOnResize = navigationRows.isBusy();
      await wait();
      const resizeBack = window.serialMonitorNavigationBack();
      const resizeBackId = resizeBack.id;
      window.serialMonitorNavigate('#1');
      window.serialMonitorNavigate('100%');
      window.serialMonitorAppendLog(Array.from({ length: 1200 }, (_, i) => line(i + 601, `packet ${i + 601} ` + 'abcdefghij'.repeat(5))).join(''), true, 4);
      await wait();
      const expired = window.serialMonitorNavigationBack();
      const trimmedFirstId = navigationEngine.resolve('#1', 0).id;

      // Neither repeated histogram rendering nor direct jumps may scan cold text.
      const oldLogical = getLogicalLineStartRows;
      getLogicalLineStartRows = () => { throw Error('Navigation scanned whole terminal history'); };
      const start = performance.now();
      for (let n = 0; n < 50; n++) {
        navigationEngine.histogram(700);
        navigationRows.viewportRange();
        navigationRows.currentIndex();
      }
      const refreshMs = performance.now() - start;
      const indexedJump = window.serialMonitorNavigate('50%');
      getLogicalLineStartRows = oldLogical;
      window.serialMonitorClear();
      await wait();
      const cleared = window.serialMonitorGetNavigationState();

      // A Back anchor inside a long wrapped line must stay inside that same
      // logical log after widening the window (its old physical offset shrinks).
      terminal.resize(20, 12);
      await wait();
      window.serialMonitorAppendLog(Array.from({ length: 10 }, (_, i) => line(i + 1, `long ${i + 1} ` + 'x'.repeat(300))).join(''), false, 5);
      await wait();
      terminal.scrollToLine(navigationRows.rowAt(2) + 7);
      const wrappedAnchor = captureNavigationAnchor();
      window.serialMonitorNavigate('#1');
      terminal.resize(100, 12);
      await wait();
      const wrappedBack = window.serialMonitorNavigationBack();
      const wrappedRow = navigationRows.rowAt(wrappedBack.index);
      const wrappedNextRow = navigationRows.rowAt(wrappedBack.index + 1);
      const wrappedViewport = terminal.buffer.active.viewportY;

      // A complete replacement keeps stable Back IDs and remains unavailable
      // while chunks are staged, before parsing even starts.
      window.serialMonitorNavigate('#2');
      const replacementAnchor = navigationBackStack[navigationBackStack.length - 1].id;
      window.serialMonitorBeginReplaceLog();
      const stagedJump = window.serialMonitorNavigate('#1');
      window.serialMonitorQueueReplaceChunk(Array.from({ length: 10 }, (_, i) => line(i + 1, `restored ${i + 1}`)).join(''));
      window.serialMonitorCommitReplaceLog(false, 6);
      await wait();
      const replaced = window.serialMonitorNavigate('#5');
      const restoredText = currentText(replaced.index);
      const preservedHistoryId = navigationBackStack[navigationBackStack.length - 2]?.id;

      // Reflow must preserve the logical boundary of the first newly received
      // log after narrowing a full buffer, including wide cells and tabs.
      window.serialMonitorClear();
      terminal.resize(88, 23);
      await wait();
      window.serialMonitorAppendLog(Array.from({ length: 800 }, (_, i) =>
        line(i + 1, `ID${i + 1} ` + '가😀x\t'.repeat(17))).join(''), false, 7);
      await wait();
      terminal.resize(34, 23);
      await wait();
      window.serialMonitorAppendLog(line(801, 'ID801 ' + '가😀x\t'.repeat(8)), false, 8);
      await wait();
      terminal.resize(91, 6);
      await wait();
      const reflowCount = navigationEngine.range().count;
      let reflowMismatches = 0;
      for (let index = 0; index < reflowCount; index++) {
        const metadata = navigationRawSource.at(index + navigationRows.sourceStart());
        if (!currentText(index)?.startsWith(`ID${metadata.id} `)) reflowMismatches++;
      }
      const reflowJump = window.serialMonitorNavigateToIndex(0);
      const reflowText = currentText(reflowJump.index);
      const reflowTail = window.serialMonitorNavigateToIndex(-1, '801');
      const reflowTailText = currentText(reflowTail.index);

      // Return history state with the action response, so an expired final
      // anchor disables the native Back button without another snapshot read.
      window.serialMonitorClear();
      terminal.resize(88, 23);
      await wait();
      window.serialMonitorAppendLog(Array.from({ length: 100 }, (_, i) => line(i + 1)).join(''), true, 9);
      await wait();
      window.serialMonitorNavigate('#1');
      window.serialMonitorAppendLog(Array.from({ length: 2000 }, (_, i) => line(i + 101)).join(''), false, 10);
      await wait();
      const expiredFinalBack = window.serialMonitorNavigationBack();
      const exhaustedBack = window.serialMonitorNavigationBack();
      return { jump, targetText, initialTop, targetViewport, back, restoredTop, relativeDifference,
        percent, percentCount, pausedViewport, viewportAfterRx, invalid, retained, oldest, oldestRow,
        oldestText, oldId, busyOnResize, resizeBack, resizeBackId, sourceAnchor, expired, trimmedFirstId,
        refreshMs, indexedJump, cleared, wrappedAnchor, wrappedBack, wrappedRow, wrappedNextRow,
        wrappedViewport, replacementAnchor, stagedJump, replaced, restoredText, preservedHistoryId,
        reflowCount, reflowMismatches, reflowJump, reflowText, reflowTail, reflowTailText,
        expiredFinalBack, exhaustedBack,
        messages: window.navigationMessages.filter(x => /navigation/.test(x.type)) };
    });
    assert.equal(result.jump.ok, true);
    assert.equal(result.jump.index, 99);
    assert.equal(result.targetText, 'packet 100');
    assert.equal(result.back.ok, true);
    assert.equal(result.restoredTop, result.initialTop);
    assert.equal(result.relativeDifference, 30);
    assert.equal(result.percent.index, Math.round((result.percentCount - 1) * 0.7));
    assert.equal(result.viewportAfterRx, result.pausedViewport);
    assert.equal(result.invalid.ok, false);
    assert.equal(result.oldest.ok, true);
    assert.ok(result.retained.count < 600, 'Physical wrapping must bound available logs');
    assert.ok(result.oldestRow >= 0);
    assert.equal(result.oldestText, (`packet ${result.oldId} ` + 'abcdefghij'.repeat(5)).slice(0, 25));
    assert.equal(result.busyOnResize, true);
    assert.equal(result.resizeBack.ok, true);
    assert.equal(result.resizeBackId, result.sourceAnchor.id);
    assert.equal(result.expired.error, 'expired');
    assert.ok(Number(result.trimmedFirstId) > Number(result.oldId));
    assert.equal(result.indexedJump.ok, true);
    assert.equal(result.cleared.count, 0);
    assert.equal(result.cleared.canGoBack, false);
    assert.equal(result.wrappedBack.id, result.wrappedAnchor.id);
    assert.ok(result.wrappedViewport >= result.wrappedRow && result.wrappedViewport < result.wrappedNextRow,
      'Back after widening must restore within the anchored logical log');
    assert.equal(result.stagedJump.error, 'busy');
    assert.equal(result.replaced.ok, true);
    assert.equal(result.restoredText, 'restored 5');
    assert.equal(result.preservedHistoryId, result.replacementAnchor);
    assert.equal(result.reflowCount, 201);
    assert.equal(result.reflowMismatches, 0);
    assert.equal(result.reflowJump.ok, true);
    assert.ok(result.reflowText.startsWith(`ID${result.reflowJump.id} `));
    assert.equal(result.reflowTail.ok, true);
    assert.ok(result.reflowTailText.startsWith('ID801 '));
    assert.equal(result.expiredFinalBack.error, 'expired');
    assert.equal(result.expiredFinalBack.canGoBack, false);
    assert.equal(result.exhaustedBack.error, 'no-history');
    assert.equal(result.exhaustedBack.canGoBack, false);
    assert.ok(result.messages.some(x => x.type === 'navigationJump'));
    assert.ok(!result.messages.some(x => x.type === 'navigationError'));
    console.log(JSON.stringify({ ...result, messages: result.messages.length }, null, 2));

    if (process.argv.includes('--million')) {
      const million = await page.evaluate(async () => {
        window.serialMonitorSetScrollback(1000000);
        terminal.resize(120, 30);
        window.serialMonitorClear();
        const base = 1793149200000;
        const began = performance.now();
        for (let batch = 0; batch < 1000; batch++) {
          const text = Array.from({ length: 1000 }, (_, i) => {
            const id = batch * 1000 + i + 1;
            return `\x1b]777;${id},${base + id}\x07packet ${id}\r\n`;
          }).join('');
          await new Promise(resolve => terminal.write(text, resolve));
          onTerminalWriteParsed();
        }
        while (navigationRows.isBusy()) await new Promise(resolve => setTimeout(resolve, 5));
        const loadedMs = performance.now() - began;
        const measure = action => { const start = performance.now(); const value = action(); return { ms: performance.now() - start, value }; };
        const graph = measure(() => { const h = navigationEngine.histogram(1000); return { count: h.count, bins: h.bins.length }; });
        const jump = measure(() => window.serialMonitorNavigate('#500000'));
        const row = navigationRows.rowAt(jump.value.index);
        const text = terminal.buffer.active.getLine(row).translateToString(true);
        const viewport = measure(() => navigationRows.viewportRange());
        return { loadedMs, retained: navigationEngine.range().count, graph, jump, viewport, text };
      });
      assert.equal(million.retained, 1000000);
      assert.equal(million.graph.value.count, 1000000);
      assert.equal(million.jump.value.ok, true);
      assert.equal(million.text, 'packet 500000');
      console.log('MILLION', JSON.stringify(million, null, 2));
    }
    assert.deepEqual(errors, []);
    console.log('PASS real xterm navigation, retention, RX, resize, history, clear' + (process.argv.includes('--million') ? ', 1M lines' : ''));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
