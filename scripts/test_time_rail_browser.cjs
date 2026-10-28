// Real Edge checks for the thin vertical time rail. NODE_PATH can point to the bundled runtime.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const errors = [];
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 }, timezoneId: 'Asia/Seoul' });
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.railMessages = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = { postMessage: message => window.railMessages.push(message) };
    });
    const assets = path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm');
    await page.goto(pathToFileURL(path.join(assets, 'index.html')).href + '?language=en');
    await page.waitForSelector('#timeRail');
    await page.waitForTimeout(300);
    const setup = await page.evaluate(async () => {
      window.railHistogramCalls = 0;
      navigationEngine.histogram = navigationEngine.histogramRange = () => {
        window.railHistogramCalls++;
        throw Error('The time rail must not request histograms');
      };
      getLogicalLineStartRows = () => { throw Error('The time rail scanned the complete terminal history'); };
      window.railIdle = async () => {
        const deadline = Date.now() + 10000;
        while (Date.now() < deadline) {
          const queue = window.serialMonitorGetAppendQueueState();
          if (!queue.writing && !queue.queueLength && !window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).busy) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('Time rail fixture did not settle');
      };
      window.railBase = new Date(2026, 9, 28, 10, 0, 0).getTime();
      window.railLine = (id, timestamp = window.railBase + id * 1000) =>
        `\x1b]777;${id},${timestamp}\x07packet ${id}\r\n`;
      window.serialMonitorSetScrollback(1000);
      window.serialMonitorClear();
      window.serialMonitorSetAutoScrollEnabled(true);
      window.serialMonitorAppendLog(Array.from({ length: 700 }, (_, index) => window.railLine(index + 1)).join(''), true, 1);
      await window.railIdle();
      const rail = document.getElementById('timeRail').getBoundingClientRect();
      const terminal = document.getElementById('terminal').getBoundingClientRect();
      return { railWidth: rail.width, railHeight: rail.height, railRight: rail.right,
        terminalLeft: terminal.left, terminalTop: terminal.top, terminalBottom: terminal.bottom,
        pageHeight: innerHeight, htmlDialogs: document.querySelectorAll('.log-jump-overlay,[role="dialog"]').length,
        horizontalActivity: document.querySelectorAll('.log-navigation,#activityPlot,.activity-graph').length };
    });
    assert.ok(setup.railWidth > 0 && setup.railWidth <= 24);
    assert.equal(setup.terminalTop, 0);
    assert.equal(setup.terminalBottom, setup.pageHeight);
    assert.equal(setup.railHeight, setup.pageHeight);
    assert.ok(setup.terminalLeft >= setup.railRight - 1);
    assert.equal(setup.htmlDialogs, 0);
    assert.equal(setup.horizontalActivity, 0);

    const rail = page.locator('#timeRail');
    await page.waitForFunction(() => document.getElementById('timeRail').getAttribute('aria-disabled') === 'false');
    assert.equal(await rail.getAttribute('data-mode'), 'time', 'Payload-only rows still navigate by their independent OSC timestamps');
    let box = await rail.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(100);
    const hover = await page.locator('#timeRailTooltip').innerText();
    assert.match(hover, /2026-10-28/);
    assert.match(hover, /10:05:5[01]\.000/, 'The tooltip resolves an actual local log timestamp');
    const messagesBefore = await page.evaluate(() => window.railMessages.filter(message => message.type === 'navigationJump').length);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(300);
    const jumped = await page.evaluate(async () => {
      const before = terminal.buffer.active.viewportY;
      const following = document.getElementById('autoScrollMenuItem').getAttribute('aria-checked');
      window.serialMonitorAppendLog(window.railLine(701), true, 2);
      await window.railIdle();
      return { before, after: terminal.buffer.active.viewportY, following,
        jumps: window.railMessages.filter(message => message.type === 'navigationJump').length,
        bandHidden: document.querySelector('.time-rail-viewport').hidden,
        bandDisplay: getComputedStyle(document.querySelector('.time-rail-viewport')).display };
    });
    assert.equal(jumped.jumps, messagesBefore + 1);
    assert.equal(jumped.following, 'false');
    assert.equal(jumped.after, jumped.before);
    assert.equal(jumped.bandHidden, false);
    assert.notEqual(jumped.bandDisplay, 'none');

    await rail.focus();
    await page.keyboard.press('Home');
    const homeValue = await rail.getAttribute('aria-valuenow');
    await page.keyboard.press('ArrowDown');
    const arrowValue = await rail.getAttribute('aria-valuenow');
    assert.ok(Number(arrowValue) > Number(homeValue), 'ArrowDown moves toward later logs');
    await page.keyboard.press('Home');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(50);
    assert.equal(await page.evaluate(() => terminal.buffer.active.viewportY), 0);
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(50);
    assert.equal(await page.evaluate(() => terminal.buffer.active.viewportY), await page.evaluate(() => terminal.buffer.active.baseY));
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#timeRailTooltip').isVisible(), false);

    await page.evaluate(async () => {
      window.serialMonitorClear();
      window.serialMonitorAppendLog(window.railLine(1), false, 3);
      await window.railIdle();
    });
    await page.waitForTimeout(300);
    box = await rail.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.7);
    await page.waitForTimeout(100);
    assert.match(await page.locator('#timeRailTooltip').innerText(), /10:00:01\.000/);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height * 0.7);
    await page.evaluate(async () => {
      window.serialMonitorClear();
      const times = [1000, 5000, 2000, 4000, 3000];
      window.serialMonitorAppendLog(times.map((offset, index) => window.railLine(index + 1, window.railBase + offset)).join(''), false, 4);
      await window.railIdle();
    });
    await page.waitForTimeout(300);
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(100);
    assert.match(await page.locator('#timeRailTooltip').innerText(), /10:00:05\.000/,
      'Clock regression tooltip reflects the actual destination, not an invented interpolated timestamp');

    // Older/untagged timestamp metadata must not make retained logs unreachable.
    await page.evaluate(async () => {
      window.serialMonitorClear();
      const text = Array.from({ length: 300 }, (_, index) => `\x1b]777;${index + 1}\x07payload without a time ${index + 1}\r\n`).join('');
      window.serialMonitorAppendLog(text, true, 5);
      await window.railIdle();
    });
    await page.waitForTimeout(300);
    assert.equal(await rail.getAttribute('data-mode'), 'position');
    assert.equal(await rail.getAttribute('aria-disabled'), 'false');
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(100);
    const fallbackHover = await page.locator('#timeRailTooltip').innerText();
    assert.match(fallbackHover, /#151 \/ 300/);
    assert.match(fallbackHover, /Time unavailable/);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    const ordinalJump = await page.evaluate(async () => {
      const currentIndex = window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).currentIndex;
      const before = terminal.buffer.active.viewportY;
      window.serialMonitorAppendLog('\x1b]777;301\x07new payload without a time\r\n', true, 6);
      await window.railIdle();
      return { currentIndex, before, after: terminal.buffer.active.viewportY,
        following: document.getElementById('autoScrollMenuItem').getAttribute('aria-checked'),
        bandVisible: !document.querySelector('.time-rail-viewport').hidden };
    });
    assert.equal(ordinalJump.currentIndex, 150);
    assert.equal(ordinalJump.before, ordinalJump.after);
    assert.equal(ordinalJump.following, 'false');
    assert.equal(ordinalJump.bandVisible, true);
    await rail.focus();
    await page.keyboard.press('Home');
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => terminal.buffer.active.viewportY), 0);
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => terminal.buffer.active.viewportY), await page.evaluate(() => terminal.buffer.active.baseY));
    const ordinalRetention = await page.evaluate(async () => {
      const anchor = window.serialMonitorResolveIndex(150);
      const rows = (first, count) => Array.from({ length: count }, (_, offset) =>
        `\x1b]777;${first + offset}\x07retained payload ${first + offset}\r\n`).join('');
      window.serialMonitorAppendLog(rows(302, 800), true, 60);
      await window.railIdle();
      const retained = window.serialMonitorNavigateToIndex(anchor.index, anchor.id);
      window.serialMonitorAppendLog(rows(1102, 1100), true, 61);
      await window.railIdle();
      const expired = window.serialMonitorNavigateToIndex(anchor.index, anchor.id);
      return { anchor, retained, expired };
    });
    assert.equal(ordinalRetention.retained.ok, true);
    assert.equal(ordinalRetention.retained.id, ordinalRetention.anchor.id);
    assert.ok(ordinalRetention.retained.index < ordinalRetention.anchor.index, 'A retained ordinal anchor follows its ID as older rows leave');
    assert.equal(ordinalRetention.expired.error, 'expired', 'An evicted ordinal ID must never jump to the replacement row');
    await page.evaluate(async () => {
      window.serialMonitorClear();
      window.serialMonitorAppendLog(Array.from({ length: 80 }, (_, index) => window.railLine(index + 1, window.railBase)).join(''), false, 7);
      await window.railIdle();
    });
    await page.waitForTimeout(300);
    assert.equal(await rail.getAttribute('data-mode'), 'position', 'Equal timestamps still allow selecting different log positions');
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(100);
    const sameTimeHover = await page.locator('#timeRailTooltip').innerText();
    assert.match(sameTimeHover, /10:00:00\.000/);
    assert.match(sameTimeHover, /#41 \/ 80/);
    await page.evaluate(async () => {
      window.serialMonitorClear();
      window.serialMonitorAppendLog('\x1b]777;1\x07one log without a time\r\n', false, 8);
      await window.railIdle();
    });
    await page.waitForTimeout(300);
    assert.equal(await rail.getAttribute('data-mode'), 'position');
    assert.equal(await rail.getAttribute('aria-disabled'), 'false');
    await page.mouse.move(box.x + box.width / 2, box.y + box.height * 0.7);
    await page.waitForTimeout(100);
    assert.match(await page.locator('#timeRailTooltip').innerText(), /#1 \/ 1/);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height * 0.7);

    await page.evaluate(() => window.serialMonitorSetColorScheme('dark'));
    const darkColor = await rail.evaluate(element => getComputedStyle(element).backgroundColor);
    await page.evaluate(() => window.serialMonitorSetColorScheme('light'));
    const lightColor = await rail.evaluate(element => getComputedStyle(element).backgroundColor);
    assert.notEqual(darkColor, lightColor);
    await page.setViewportSize({ width: 340, height: 320 });
    await page.waitForTimeout(350);
    const narrow = await rail.boundingBox();
    assert.ok(narrow.width <= 24 && narrow.height === 320);
    await page.emulateMedia({ forcedColors: 'active' });
    assert.ok(await rail.isVisible());
    await page.evaluate(() => window.serialMonitorClear());
    await page.waitForTimeout(350);
    assert.equal(await rail.getAttribute('aria-disabled'), 'true');
    assert.equal(await page.evaluate(() => window.railHistogramCalls), 0);

    // Exercise one million logical positions without allocating a second terminal
    // history: the rail consumes range metadata, never all rows or histogram bins.
    const synthetic = await browser.newPage({ viewport: { width: 800, height: 600 }, timezoneId: 'Asia/Seoul' });
    synthetic.on('pageerror', error => errors.push(error.message));
    await synthetic.setContent('<div id="terminal" style="position:absolute;inset:0"></div>');
    await synthetic.addStyleTag({ path: path.join(assets, 'time-rail.css') });
    await synthetic.addScriptTag({ path: path.join(assets, 'time-rail.js') });
    const million = await synthetic.evaluate(async () => {
      let reads = 0;
      const base = 1793149200000;
      const rail = SerialMonitorTimeRail.install({
        getState() { reads++; return { count: 1000000, startTime: base, endTime: base + 999999000,
          viewportStartTime: base + 499990000, viewportEndTime: base + 500010000, busy: false }; },
        resolveTime: timestamp => ({ ok: true, timestamp: Math.ceil((timestamp - base) / 1000) * 1000 + base }),
        navigateTime: timestamp => ({ ok: true, timestamp }),
        onLayoutChanged() {}, onError(error) { throw error; }, language: 'en'
      });
      const start = performance.now();
      for (let index = 0; index < 1000; index++) rail.refresh();
      const refreshMilliseconds = performance.now() - start;
      await new Promise(resolve => setTimeout(resolve, 300));
      const width = document.getElementById('timeRail').getBoundingClientRect().width;
      const output = { reads, width, refreshMilliseconds };
      rail.dispose();
      return output;
    });
    assert.ok(million.reads <= 5, 'Throttled rail refresh reads bounded metadata, independent of one million positions');
    assert.ok(million.width > 0 && million.width <= 24);
    assert.ok(million.refreshMilliseconds < 200, 'Scheduling a rail refresh must not scan logical positions');
    const edgeStates = await synthetic.evaluate(async () => {
      let state = { count: 100, startTime: null, endTime: null, viewportStartIndex: 40, viewportEndIndex: 60, busy: false };
      let epoch = 'old';
      let calls = 0;
      let expectedId;
      const rail = SerialMonitorTimeRail.install({
        getState: () => state,
        resolveTime: timestamp => ({ ok: true, timestamp, index: 0, id: `${epoch}-0` }),
        navigateTime: timestamp => { calls++; return { ok: true, timestamp }; },
        resolveIndex: index => ({ ok: true, index, timestamp: null, id: `${epoch}-${index}` }),
        navigateIndex(index, id) {
          calls++; expectedId = id;
          return id && id !== `${epoch}-${index}` ? { ok: false, error: 'expired' }
            : { ok: true, index, timestamp: null, id: `${epoch}-${index}` };
        },
        onLayoutChanged() {}, onError(error) { throw error; }, language: 'ko'
      });
      const root = document.getElementById('timeRail');
      const hint = document.getElementById('timeRailTooltip');
      const preview = async fraction => {
        root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Home', bubbles: true }));
        const bounds = root.getBoundingClientRect();
        root.dispatchEvent(new PointerEvent('pointermove', { clientY: fraction * bounds.height, bubbles: true }));
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      };
      await preview(0.5);
      const koreanHint = hint.textContent;
      epoch = 'new';
      root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      const expiredHint = hint.textContent;
      const callsAfterExpired = calls;
      state = { ...state, busy: true };
      rail.refresh();
      await new Promise(resolve => setTimeout(resolve, 300));
      root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      const callsWhileBusy = calls;
      const cases = [];
      for (const pair of [[1000.5, 2000.5], [Number.NaN, 2000], [1e15, 1e15 + 1000], [2000, 1000]]) {
        state = { count: 10, startTime: pair[0], endTime: pair[1], viewportStartIndex: 0, viewportEndIndex: 9, busy: false };
        rail.refresh();
        await new Promise(resolve => setTimeout(resolve, 260));
        await preview(0.5);
        cases.push({ mode: root.dataset.mode, enabled: root.getAttribute('aria-disabled'), text: hint.textContent });
      }
      state = { count: 1, startTime: 0, endTime: 0, viewportStartTime: 0, viewportEndTime: 0, busy: false };
      rail.refresh();
      await new Promise(resolve => setTimeout(resolve, 260));
      await preview(0.5);
      const zeroTime = { mode: root.dataset.mode, text: hint.textContent };
      rail.dispose();
      return { koreanHint, expiredHint, expectedId, callsAfterExpired, callsWhileBusy, cases, zeroTime };
    });
    assert.match(edgeStates.koreanHint, /#51 \/ 100.*시각 정보 없음/);
    assert.match(edgeStates.expiredHint, /보관 범위/);
    assert.equal(edgeStates.expectedId, 'old-50', 'Ordinal click uses the hovered stable ID across retention changes');
    assert.equal(edgeStates.callsWhileBusy, edgeStates.callsAfterExpired, 'Busy reindexing never executes a stale jump');
    for (const item of edgeStates.cases) {
      assert.equal(item.mode, 'position');
      assert.equal(item.enabled, 'false');
      assert.match(item.text, /시각 정보 없음/);
      assert.doesNotMatch(item.text, /NaN|Invalid Date/);
    }
    assert.equal(edgeStates.zeroTime.mode, 'time');
    assert.match(edgeStates.zeroTime.text, /1970-01-01/);

    // A reviewable component preview uses the same shipped terminal and time
    // rail, with timestamped mock logs; no horizontal UI is added for capture.
    await page.emulateMedia({ forcedColors: 'none' });
    await page.setViewportSize({ width: 740, height: 450 });
    await page.evaluate(async () => {
      window.serialMonitorSetColorScheme('dark');
      window.serialMonitorClear();
      window.serialMonitorSetAutoScrollEnabled(true);
      const two = value => String(value).padStart(2, '0');
      const lines = Array.from({ length: 360 }, (_, index) => {
        const id = index + 1;
        const timestamp = window.railBase + id * 1000;
        const date = new Date(timestamp);
        const time = `${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}.000`;
        const content = `[2026-10-28 ${time}] RX < seq=${String(id).padStart(4, '0')} temp=25.${id % 10} voltage=3.300 status=OK`;
        return `\x1b]777;${id},${timestamp}\x07${content}\r\n`;
      });
      window.serialMonitorAppendLog(lines.join(''), true, 50);
      await window.railIdle();
    });
    await page.waitForFunction(() => document.getElementById('timeRail').getAttribute('aria-disabled') === 'false');
    await page.waitForTimeout(300);
    const previewJump = await page.evaluate(() => window.serialMonitorNavigate('50%'));
    assert.equal(previewJump.ok, true);
    await page.waitForTimeout(300);
    const previewRail = await rail.boundingBox();
    await page.mouse.move(previewRail.x + previewRail.width / 2, previewRail.y + previewRail.height / 2);
    await page.waitForTimeout(100);
    assert.equal(await page.locator('#timeRailTooltip').isVisible(), true);
    const artifacts = path.resolve(__dirname, '../artifacts');
    fs.mkdirSync(artifacts, { recursive: true });
    const previewPath = path.join(artifacts, 'time-rail-preview.png');
    const detailPath = path.join(artifacts, 'time-rail-detail.png');
    await page.screenshot({ path: previewPath });
    await page.screenshot({ path: detailPath, clip: { x: 0, y: 0, width: 280, height: 450 } });
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ setup, hover, jumped, fallbackHover, ordinalJump, ordinalRetention, sameTimeHover,
      narrow, million, edgeStates, previewPath, detailPath, errors }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
