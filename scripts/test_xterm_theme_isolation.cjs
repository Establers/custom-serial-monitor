// Requires Playwright (NODE_PATH may point at the bundled runtime packages).
// Uses two pages in one isolated browser context, sharing browser preferences.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

const themes = {
  dark: { background: '#0c0d0e', foreground: '#d6d6d6', pageBackground: 'rgb(12, 13, 14)', pageForeground: 'rgb(214, 214, 214)' },
  light: { background: '#ffffff', foreground: '#1b1b1b', pageBackground: 'rgb(255, 255, 255)', pageForeground: 'rgb(27, 27, 27)' }
};

async function readTheme(page) {
  return page.evaluate(() => ({
    background: terminal.options.theme.background,
    foreground: terminal.options.theme.foreground,
    pageBackground: getComputedStyle(document.body).backgroundColor,
    pageForeground: getComputedStyle(document.getElementById('contextMenu')).color
  }));
}

async function assertTheme(page, theme) {
  await page.waitForFunction(background => terminal.options.theme.background === background, themes[theme].background);
  assert.deepEqual(await readTheme(page), themes[theme]);
  assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme), theme);
}

async function setTheme(page, theme) {
  assert.equal(await page.evaluate(value => window.serialMonitorSetColorScheme(value), theme), true);
}

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const context = await browser.newContext({ colorScheme: 'dark', viewport: { width: 1000, height: 700 } });
    const first = await context.newPage();
    const second = await context.newPage();
    const errors = [];
    for (const page of [first, second]) page.on('pageerror', error => errors.push(error.message));
    const url = pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href;
    await first.goto(`${url}?colorScheme=light`);
    await second.goto(`${url}?colorScheme=dark`);
    await assertTheme(first, 'light');
    await assertTheme(second, 'dark');

    await first.evaluate(() => {
      window.serialMonitorAppendLog('retained log\r\n', false, 1);
    });
    await first.waitForFunction(() => !window.serialMonitorGetAppendQueueState().writing);
    await first.evaluate(() => terminal.select(0, 0, 8));

    // Changing either instance must leave the other instance's theme intact.
    await setTheme(second, 'light');
    await setTheme(first, 'dark');
    await assertTheme(first, 'dark');
    await assertTheme(second, 'light');
    await setTheme(second, 'dark');
    await assertTheme(first, 'dark');
    await setTheme(first, 'light');
    await assertTheme(first, 'light');
    await assertTheme(second, 'dark');
    assert.equal(await first.evaluate(() => terminal.buffer.active.getLine(0).translateToString(true)), 'retained log');
    assert.equal(await first.evaluate(() => terminal.getSelection()), 'retained');

    // Simulate a shared browser preference changing in both pages. Explicit
    // window themes must win over that preference, including a saved stale one.
    for (const colorScheme of ['light', 'dark']) {
      await Promise.all([first, second].map(page => page.emulateMedia({ colorScheme })));
      await assertTheme(first, 'light');
      await assertTheme(second, 'dark');
    }
    await first.reload();
    await assertTheme(first, 'light');
    await assertTheme(second, 'dark');

    // High contrast still overrides both light and dark palettes and page CSS.
    for (const page of [first, second]) {
      await page.emulateMedia({ forcedColors: 'active' });
      await page.waitForFunction(() => terminal.options.theme.background === getComputedStyle(document.body).backgroundColor);
      const theme = await readTheme(page);
      assert.equal(theme.background, theme.pageBackground);
      assert.equal(theme.foreground, theme.pageForeground);
      assert.equal(await page.evaluate(() => terminal.options.theme.extendedAnsi[0]),
        await page.evaluate(() => terminal.options.theme.selectionForeground));
      await page.emulateMedia({ forcedColors: 'none' });
    }
    await assertTheme(first, 'light');
    await assertTheme(second, 'dark');

    // Standalone pages can follow the system. The host sends its own resolved
    // light/dark theme so System mode also remains independent of the profile.
    await setTheme(second, 'system');
    await second.emulateMedia({ colorScheme: 'light' });
    await assertTheme(second, 'light');
    await second.emulateMedia({ colorScheme: 'dark' });
    await assertTheme(second, 'dark');
    await assertTheme(first, 'light');
    assert.equal(await second.evaluate(() => window.serialMonitorSetColorScheme('invalid')), false);
    await assertTheme(second, 'dark');
    assert.deepEqual(errors, []);
    console.log('Theme isolation passed: two shared-context pages, live switching, retained logs/selection, shared preferences, reload, and high contrast.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
