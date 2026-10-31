/* Targeted rule recoloring for the bundled xterm cell layout, also used by
 * compact-scrollback.js. Text, widths, Unicode and non-color ANSI flags stay
 * intact. No per-line markers, copied payloads or extra history are retained.
 */
(function (root) {
  'use strict';

  function colors(sgr) {
    const codes = sgr.split(';').map(Number);
    let foreground = 0, background = 0;
    for (let index = 0; index < codes.length; index++) {
      const code = codes[index];
      if (code === 0) { foreground = 0; background = 0; }
      else if (code === 39) foreground = 0;
      else if (code === 49) background = 0;
      else if (code >= 30 && code <= 37) foreground = 0x1000000 | (code - 30);
      else if (code >= 40 && code <= 47) background = 0x1000000 | (code - 40);
      else if (code >= 90 && code <= 97) foreground = 0x1000000 | (code - 90 + 8);
      else if (code >= 100 && code <= 107) background = 0x1000000 | (code - 100 + 8);
      else if (code === 38 || code === 48) {
        let value;
        const mode = codes[++index];
        if (mode === 5 && codes[index + 1] >= 0 && codes[index + 1] <= 255) {
          value = 0x2000000 | codes[++index];
        } else if (mode === 2 && codes.slice(index + 1, index + 4).length === 3 &&
            codes.slice(index + 1, index + 4).every(n => n >= 0 && n <= 255)) {
          value = 0x3000000 | (codes[++index] << 16) | (codes[++index] << 8) | codes[++index];
        } else return null;
        if (code === 38) foreground = value;
        else background = value;
      } else return null;
    }
    return { foreground, background };
  }

  function install(terminal, resolveRows, onError = console.error) {
    const pending = new Map();
    let timer = null;
    let failed = false;

    function schedule() {
      if (pending.size && timer === null) timer = setTimeout(flush, 0);
    }

    function flush() {
      try { flushPending(); }
      catch (error) {
        failed = true;
        reset();
        onError(error);
      }
    }

    function flushPending() {
      if (timer !== null) clearTimeout(timer);
      timer = null;
      const lines = terminal._core?._bufferService?.buffers?.normal?.lines;
      const deadline = performance.now() + 4;
      let changed = false;
      for (const [id, update] of pending) {
        const range = resolveRows(id);
        if (range === undefined) break; // Anchor reflow runs in bounded slices.
        const color = update.color;
        if (range && lines) {
          let row = update.generation === range.generation
            ? Math.max(range.start, update.nextRow - range.trimmedRows) : range.start;
          for (; row < range.end; row++) {
            const line = lines.get(row);
            const data = line?._data;
            if (!(data instanceof Uint32Array) || data.length !== line.length * 3)
              throw new Error('Unsupported xterm cell layout for line highlighting');
            const length = line.getTrimmedLength();
            for (let cell = 0; cell < length; cell++) {
              const offset = cell * 3;
              data[offset + 1] = (data[offset + 1] & ~0x3ffffff) | color.foreground;
              data[offset + 2] = (data[offset + 2] & ~0x3ffffff) | color.background;
            }
            changed = true;
            if (performance.now() >= deadline) {
              update.generation = range.generation;
              update.nextRow = row + 1 + range.trimmedRows;
              break;
            }
          }
          if (row >= range.end) pending.delete(id);
        } else {
          pending.delete(id);
        }
        if (performance.now() >= deadline) break;
      }
      if (changed) terminal.refresh(0, terminal.rows - 1);
      schedule();
    }

    function reset() {
      pending.clear();
      if (timer !== null) clearTimeout(timer);
      timer = null;
    }

    const osc = terminal.parser.registerOscHandler(778, data => {
      if (failed) return true;
      const match = /^(\d+);(\d+(?:;\d+)*)$/.exec(data);
      if (!match || match[2].length > 96) return false;
      const color = colors(match[2]);
      if (!color) return false;
      pending.set(match[1], { color });
      // Old pending IDs cannot outlive the terminal's bounded metadata ring.
      const limit = Math.max(1, Number(terminal.options.scrollback || 0) + terminal.rows);
      while (pending.size > limit) pending.delete(pending.keys().next().value);
      schedule();
      return true;
    });
    const parsed = terminal.onWriteParsed(flush);
    return { reset, pendingCount: () => pending.size, dispose() { reset(); osc.dispose(); parsed.dispose(); } };
  }

  root.SerialMonitorLineHighlighting = { install };
})(globalThis);
