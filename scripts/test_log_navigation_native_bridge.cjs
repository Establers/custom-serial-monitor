// Real Edge regressions for the native navigation tab/dialog's terminal bridge.
// NODE_PATH may point to the bundled runtime's node_modules.
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
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.waitForTimeout(200);
    const result = await page.evaluate(async () => {
      const idle = async () => {
        const deadline = Date.now() + 10000;
        while (Date.now() < deadline) {
          const queue = window.serialMonitorGetAppendQueueState();
          if (!queue.writing && !queue.queueLength && !window.serialMonitorGetNavigationSnapshot().busy) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('Terminal navigation did not settle');
      };
      const base = new Date(2026, 9, 28, 10, 0, 0).getTime();
      const line = id => `\x1b]777;${id},${base + id * 1000}\x07packet ${id}\r\n`;
      const terminalRect = document.getElementById('terminal').getBoundingClientRect();
      const terminalBox = { top: terminalRect.top, bottom: terminalRect.bottom, height: terminalRect.height, pageHeight: innerHeight };
      const htmlControls = document.querySelectorAll('.log-navigation,.log-jump-overlay,[role="dialog"]').length;
      window.serialMonitorSetScrollback(1000);
      window.serialMonitorClear();
      const empty = window.serialMonitorGetNavigationSnapshot({ binCount: 600 });
      const emptyPreview = window.serialMonitorResolveNavigation('#1');
      window.serialMonitorAppendLog(Array.from({ length: 700 }, (_, index) => line(index + 1)).join(''), true, 1);
      await idle();
      const before = {
        viewportY: terminal.buffer.active.viewportY,
        autoScroll: document.getElementById('autoScrollMenuItem').getAttribute('aria-checked'),
        selection: terminal.getSelection(),
        messages: window.navigationMessages.length
      };
      const preview = window.serialMonitorResolveNavigation('10:01:40');
      const afterPreview = {
        viewportY: terminal.buffer.active.viewportY,
        autoScroll: document.getElementById('autoScrollMenuItem').getAttribute('aria-checked'),
        selection: terminal.getSelection(),
        messages: window.navigationMessages.length
      };
      const snapshot = window.serialMonitorGetNavigationSnapshot({ binCount: 600 });
      const largeBins = window.serialMonitorGetNavigationSnapshot({ binCount: 1e12 });
      const zoom = window.serialMonitorGetNavigationSnapshot({ binCount: 350, startTime: base + 200000, endTime: base + 250000 });
      const readOnlyBackState = window.serialMonitorGetNavigationSnapshot().canGoBack;
      const jump = window.serialMonitorNavigate('10:01:40');
      const jumpViewport = terminal.buffer.active.viewportY;
      const followingAfterJump = document.getElementById('autoScrollMenuItem').getAttribute('aria-checked');
      window.serialMonitorAppendLog(line(701), true, 2);
      await idle();
      const viewportAfterRx = terminal.buffer.active.viewportY;
      const back = window.serialMonitorNavigationBack();
      const backViewport = terminal.buffer.active.viewportY;
      const timeJump = window.serialMonitorNavigateToTime(base + 123450);
      const relativeBase = window.serialMonitorGetNavigationSnapshot().currentIndex;
      const relative = window.serialMonitorNavigate('+30s');
      const percent = window.serialMonitorNavigate('70%');
      const latest = window.serialMonitorGetNavigationSnapshot({ binCount: 64 });
      const invalidPreview = window.serialMonitorResolveNavigation('2026-02-30 10:00');
      const invalidTimeJump = window.serialMonitorNavigateToTime(base - 1);
      const viewportBeforeOpening = terminal.buffer.active.viewportY;
      const opened = window.serialMonitorOpenNavigation();
      const noHtmlAfterOpen = document.querySelectorAll('.log-navigation,.log-jump-overlay,[role="dialog"]').length;
      const viewportAfterOpening = terminal.buffer.active.viewportY;
      return { terminalBox, htmlControls, empty, emptyPreview, before, preview, afterPreview,
        snapshot, largeBinCount: largeBins.bins.length, zoom, readOnlyBackState,
        jump, jumpViewport, followingAfterJump, viewportAfterRx, back, backViewport,
        timeJump, relativeBase, relative, percent, latest, invalidPreview, invalidTimeJump,
        opened, noHtmlAfterOpen, viewportBeforeOpening, viewportAfterOpening,
        navigationErrors: window.navigationMessages.filter(message => message.type === 'navigationError') };
    });
    assert.equal(result.htmlControls, 0, 'Terminal must not install an HTML bar or dialog');
    assert.equal(result.noHtmlAfterOpen, 0, 'Opening navigation belongs to the native host');
    assert.equal(result.terminalBox.top, 0);
    assert.equal(result.terminalBox.bottom, result.terminalBox.pageHeight, 'Terminal keeps its full available height');
    assert.equal(result.empty.count, 0);
    assert.equal(result.emptyPreview.ok, false);
    assert.equal(result.preview.ok, true);
    assert.equal(result.preview.id, '100');
    assert.deepEqual(result.afterPreview, result.before, 'Preview must not alter scrolling, selection, following or host messages');
    assert.equal(result.readOnlyBackState, false, 'Snapshot and preview do not populate Back history');
    assert.equal(result.snapshot.count, 700);
    assert.equal(result.snapshot.bins.length, 600);
    assert.equal(result.snapshot.bins.reduce((sum, count) => sum + count, 0), 700);
    assert.ok(result.snapshot.peak > 0 && result.snapshot.binMilliseconds > 0);
    assert.ok(result.largeBinCount <= 4096, 'Untrusted histogram size remains bounded');
    assert.ok(result.zoom.histogramStartTime >= result.snapshot.startTime);
    assert.ok(result.zoom.histogramEndTime <= result.snapshot.endTime);
    assert.ok(result.zoom.histogramEndTime - result.zoom.histogramStartTime < result.snapshot.endTime - result.snapshot.startTime);
    assert.equal(result.jump.id, '100');
    assert.equal(result.followingAfterJump, 'false');
    assert.equal(result.viewportAfterRx, result.jumpViewport, 'New RX preserves the inspected location');
    assert.equal(result.back.ok, true);
    assert.equal(result.backViewport, result.before.viewportY);
    assert.equal(result.timeJump.id, '124', 'Time requests land on the first retained log at or after the target');
    assert.equal(result.relative.index - result.relativeBase, 30);
    assert.equal(result.percent.index, Math.round((result.latest.count - 1) * 0.7));
    assert.equal(result.invalidPreview.ok, false);
    assert.equal(result.invalidTimeJump.ok, false);
    assert.equal(result.opened, true);
    assert.equal(result.viewportBeforeOpening, result.viewportAfterOpening);
    assert.deepEqual(result.navigationErrors, []);

    const boundaries = await page.evaluate(async () => {
      const base = new Date(2026, 9, 28, 10, 0, 0).getTime();
      async function load(offsets) {
        window.serialMonitorClear();
        const text = offsets.map((offset, index) =>
          `\x1b]777;${index + 1}${offset === null ? '' : `,${base + offset}`}\x07sample ${index + 1}\r\n`).join('');
        window.serialMonitorAppendLog(text, false, 20);
        const deadline = Date.now() + 10000;
        while (Date.now() < deadline) {
          const queue = window.serialMonitorGetAppendQueueState();
          if (!queue.writing && !queue.queueLength && !window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).busy) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('Boundary fixture did not settle');
      }
      const snapshot = (start, end) => window.serialMonitorGetNavigationSnapshot({
        binCount: 48, startTime: base + start, endTime: base + end
      });
      const summarize = data => ({
        count: data.count, ordered: data.ordered,
        sum: data.bins.reduce((total, count) => total + count, 0),
        start: data.histogramStartTime, end: data.histogramEndTime,
        retainedStart: data.startTime, retainedEnd: data.endTime
      });
      await load([1000, 2000, 3000, 3000, 5000]);
      const sparseEnd = summarize(snapshot(1000, 2500));
      const emptyGap = summarize(snapshot(2501, 2999));
      const duplicates = summarize(snapshot(3000, 3000));
      const staleNewer = summarize(snapshot(6000, 7000));
      const staleOlder = summarize(snapshot(-1000, 500));
      const reversed = summarize(snapshot(4500, 1500));
      await load([1000, 4000, 2000, 4500, 3000, 6000]);
      const clockRegression = summarize(snapshot(1500, 3500));
      await load([null, null]);
      const missing = summarize(window.serialMonitorGetNavigationSnapshot({ binCount: 48 }));
      const missingPreview = window.serialMonitorResolveNavigation('10:00:00');
      return { sparseEnd, emptyGap, duplicates, staleNewer, staleOlder, reversed, clockRegression, missing, missingPreview };
    });
    assert.equal(boundaries.sparseEnd.sum, 2, 'The first sample after a fractional end boundary stays outside the histogram');
    assert.equal(boundaries.emptyGap.sum, 0, 'Zooming into a quiet time interval must not borrow a later sample');
    assert.equal(boundaries.duplicates.sum, 2, 'Every duplicate timestamp at the inclusive end boundary is counted');
    for (const name of ['staleNewer', 'staleOlder', 'reversed']) {
      assert.equal(boundaries[name].sum, 5, `${name}: stale/invalid bounds reset to the retained overview`);
      assert.equal(boundaries[name].start, boundaries[name].retainedStart);
      assert.equal(boundaries[name].end, boundaries[name].retainedEnd);
    }
    assert.equal(boundaries.clockRegression.ordered, false);
    assert.equal(boundaries.clockRegression.sum, 2, 'A time window includes matching logs across clock-regressed index segments');
    assert.equal(boundaries.missing.count, 2);
    assert.equal(boundaries.missing.sum, 0);
    assert.equal(boundaries.missing.start, null);
    assert.equal(boundaries.missing.end, null);
    assert.equal(boundaries.missingPreview.ok, false);

    await page.locator('.xterm-helper-textarea').focus();
    const countBeforeShortcut = await page.evaluate(() => window.navigationMessages.filter(message => message.type === 'openLogNavigation').length);
    await page.keyboard.press('Control+g');
    const shortcutMessages = await page.evaluate(() => window.navigationMessages.filter(message => message.type === 'openLogNavigation').length);
    assert.equal(shortcutMessages - countBeforeShortcut, 1, 'Ctrl+G dispatches exactly once to the native host');
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({
      count: result.snapshot.count, bins: result.snapshot.bins.length,
      zoomCount: result.zoom.bins.reduce((sum, count) => sum + count, 0),
      preview: result.preview, target: result.timeJump,
      terminalHeight: result.terminalBox.height, nativeShortcutDispatches: shortcutMessages - countBeforeShortcut,
      timeWindowCounts: { sparseEnd: boundaries.sparseEnd.sum, emptyGap: boundaries.emptyGap.sum,
        duplicates: boundaries.duplicates.sum, clockRegression: boundaries.clockRegression.sum },
      errors
    }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
