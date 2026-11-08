// Real terminal rendering in isolated headless Edge. Requires Playwright + pngjs.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');
const { PNG } = require('pngjs');

const imageHost = 'https://serialmonitor-background.local/';
function solidImage(color) {
  const image = new PNG({ width: 8, height: 8 });
  for (let offset = 0; offset < image.data.length; offset += 4) image.data.set([...color, 255], offset);
  return PNG.sync.write(image);
}

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 650 }, colorScheme: 'dark' });
    const errors = [];
    const requests = [];
    const red = [180, 45, 75];
    const blue = [25, 100, 210];
    let releaseSlow;
    const slow = new Promise(resolve => { releaseSlow = resolve; });
    page.on('pageerror', error => errors.push(error.message));
    await page.route(`${imageHost}**`, async route => {
      const name = new URL(route.request().url()).pathname;
      requests.push(route.request().url());
      if (name === '/missing.png') return route.fulfill({ status: 404, body: '' });
      if (name === '/corrupt.png') return route.fulfill({ contentType: 'image/png', body: 'invalid image bytes' });
      if (name === '/slow.png') await slow;
      try {
        await route.fulfill({ contentType: 'image/png', body: solidImage(name === '/blue.png' ? blue : red) });
      } catch (error) {
        if (!/closed|cancel|invalid interception/i.test(String(error))) throw error;
      }
    });
    const url = pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href;
    await page.goto(`${url}?colorScheme=dark`);
    await page.waitForSelector('.xterm-screen');
    await page.evaluate(() => {
      window.messages = [];
      window.chrome.webview = { postMessage: message => window.messages.push(message) };
      window.originalTerminal = terminal;
      window.originalBuffer = terminal.buffer.active;
      window.serialMonitorAppendLog(Array.from({ length: 2000 }, (_, i) =>
        `line ${i} 한글 \x1b[31mRED\x1b[0m\r\n`).join(''), false, 1);
    });
    await page.waitForFunction(() => window.messages.some(message => message.requestId === 1 && message.success));
    await page.evaluate(() => { terminal.scrollToTop(); terminal.select(0, 0, 6); });
    const before = await page.evaluate(() => ({
      text: terminal.buffer.active.getLine(0).translateToString(true),
      selection: terminal.getSelection(), viewport: terminal.buffer.active.viewportY,
      length: terminal.buffer.active.length, foreground: terminal.options.theme.foreground
    }));

    const set = async (source, opacity) => assert.equal(await page.evaluate(
      settings => window.serialMonitorSetBackground(settings), { source: source ? imageHost + source : '', opacity }), true);
    const visible = async expected => page.waitForFunction(value =>
      document.documentElement.dataset.terminalBackground === String(value), expected);
    const assertPixel = async (color, opacity, base = [12, 13, 14]) => {
      const png = PNG.sync.read(await page.screenshot());
      const offset = (300 * png.width + 900) * 4;
      for (let channel = 0; channel < 3; channel++) {
        const expected = color[channel] * opacity + base[channel] * (1 - opacity);
        assert.ok(Math.abs(png.data[offset + channel] - expected) <= 2,
          `Background pixel channel ${channel}: expected ${expected}, got ${png.data[offset + channel]}`);
      }
    };
    const assertLogsIntact = async () => {
      assert.deepEqual(await page.evaluate(() => ({
        text: terminal.buffer.active.getLine(0).translateToString(true),
        selection: terminal.getSelection(), viewport: terminal.buffer.active.viewportY,
        length: terminal.buffer.active.length, foreground: terminal.options.theme.foreground
      })), before);
      assert.equal(await page.evaluate(() => terminal === window.originalTerminal &&
        terminal.buffer.active === window.originalBuffer), true);
      assert.equal(await page.evaluate(() => getComputedStyle(document.getElementById('terminal')).opacity), '1');
      assert.equal(await page.evaluate(() => getComputedStyle(document.querySelector('.xterm')).opacity), '1');
    };

    await set('red.png?v=1', 0.35);
    await visible(true);
    await assertPixel(red, 0.35);
    const image = await page.locator('#terminalBackground').elementHandle();
    const loads = requests.length;
    await set('red.png?v=1', 0.75);
    await assertPixel(red, 0.75);
    assert.equal(requests.length, loads, 'Changing opacity must reuse the decoded image');
    assert.equal(await image.evaluate(node => node === document.getElementById('terminalBackground')), true);
    await assertLogsIntact();

    await set('red.png?v=1', 0);
    await visible(false);
    assert.equal(await page.evaluate(() => terminal.options.theme.background), '#0c0d0e');
    await assertPixel(red, 0);
    await set('red.png?v=1', 1);
    await visible(true);
    await assertPixel(red, 1);
    await assertLogsIntact();

    // Theme/high-contrast switches preserve the chosen image and user opacity.
    await set('red.png?v=1', 0.15);
    await page.evaluate(() => window.serialMonitorSetColorScheme('light'));
    await assertPixel(red, 0.15, [255, 255, 255]);
    await page.emulateMedia({ forcedColors: 'active' });
    await visible(false);
    assert.equal(await page.evaluate(() => terminal.options.theme.background === getComputedStyle(document.body).backgroundColor), true);
    await page.emulateMedia({ forcedColors: 'none' });
    await visible(true);
    await assertPixel(red, 0.15, [255, 255, 255]);
    await page.evaluate(() => window.serialMonitorSetColorScheme('dark'));
    await assertLogsIntact();

    // A stale image load must not overwrite a newer preset.
    await set('slow.png', 0.5);
    await set('blue.png', 0.5);
    await visible(true);
    releaseSlow();
    await assertPixel(blue, 0.5);
    assert.match(await page.locator('#terminalBackground').getAttribute('src'), /blue\.png$/);
    await assertLogsIntact();

    // In-place replacement gets a fresh URL; unavailable/corrupt images fall back.
    await set('red.png?v=2', 0.25);
    await visible(true);
    await assertPixel(red, 0.25);
    for (const [index, source] of ['missing.png', 'corrupt.png'].entries()) {
      await set(source, 0.25);
      await page.waitForFunction(count => window.messages.filter(message => message.type === 'xtermBackgroundError').length === count, index + 1);
      await visible(false);
      await assertPixel(red, 0);
      await assertLogsIntact();
    }
    await set('', 0.25);
    await visible(false);
    assert.equal(await page.locator('#terminalBackground').getAttribute('src'), null);
    for (const settings of [{ source: imageHost + 'red.png', opacity: -1 },
      { source: 'https://example.com/image.png', opacity: 0.5 }, { source: imageHost + 'red.png', opacity: null }]) {
      assert.equal(await page.evaluate(value => window.serialMonitorSetBackground(value), settings), false);
    }

    // Exercise the regular append/compaction/resize path with a visible image.
    await set('blue.png?v=stress', 0.15);
    await visible(true);
    const stress = await page.evaluate(async () => {
      terminal.clearSelection();
      window.serialMonitorSetScrollback(10000);
      const start = performance.now();
      for (let batch = 0; batch < 30; batch++) {
        window.serialMonitorAppendLog(Array.from({ length: 1000 }, (_, i) =>
          `background stress ${batch * 1000 + i} 한글 \x1b[31mRED\x1b[0m\r\n`).join(''), true, batch + 10);
        while (window.serialMonitorGetAppendQueueState().writing || window.serialMonitorGetAppendQueueState().queueLength) {
          await new Promise(resolve => setTimeout(resolve, 5));
        }
      }
      return { ms: Math.round(performance.now() - start), length: terminal.buffer.active.length,
        rows: terminal.rows, last: terminal.buffer.active.getLine(terminal.buffer.active.length - 2).translateToString(true) };
    });
    assert.equal(stress.length, 10000 + stress.rows);
    assert.equal(stress.last, 'background stress 29999 한글 RED');
    await page.setViewportSize({ width: 800, height: 500 });
    await page.waitForFunction(() => document.getElementById('terminalBackground').clientWidth === 800);
    await visible(true);
    assert.deepEqual(errors, []);
    console.log(`xterm background passed: rendered pixels, independent image opacity, retained logs/selection, image reuse/replacement, rapid switching, themes/high contrast, failure fallback, and 30,000-line append/resize (${stress.ms} ms).`);
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
