/* Physical-row anchors for bounded log navigation. Cold xterm cells stay compressed. */
(function (root) {
  'use strict';

  function create(terminal, rawSource, options = {}) {
    let trackedLines = null;
    let trimSubscription = null;
    let resizeSubscription = null;
    let trimmedRows = 0;
    let generation = 0;
    let busy = false;
    let disposed = false;
    let workTimer = null;
    let changeTimer = null;
    let cachedStart = null;
    let anchoredColumns = terminal.cols;
    const now = () => root.performance?.now?.() ?? Date.now();

    function notify() {
      cachedStart = null;
      if (changeTimer !== null || disposed) return;
      changeTimer = setTimeout(() => {
        changeTimer = null;
        if (!disposed) options.onChange?.();
      }, 0);
    }

    function attach() {
      const lines = terminal._core?._bufferService?.buffers?.normal?.lines;
      if (!lines || typeof lines.onTrim !== 'function' || typeof lines.get !== 'function') {
        throw new Error('The terminal row anchor API is unavailable.');
      }
      if (lines === trackedLines) return;
      trimSubscription?.dispose();
      trackedLines = lines;
      trimSubscription = lines.onTrim(count => {
        trimmedRows += count;
        notify();
      });
    }

    function recordRow() {
      attach();
      const buffer = terminal.buffer.active;
      const row = buffer.baseY + buffer.cursorY;
      // After narrowing a full buffer, xterm can leave the unused cursor row
      // marked as a soft wrap, with stale cells from the preceding log. OSC at
      // column zero starts a new log. Reset this unused row before text arrives
      // so tabs cannot expose stale cells and reflow cannot join the two IDs.
      // Only this live row is cleared, without scanning cold history or keeping
      // per-line markers. Ordinary RX does not take this exceptional path.
      const line = trackedLines.get(row);
      if (buffer.cursorX === 0 && line.isWrapped) {
        line.fill(terminal._core._bufferService.buffers.normal.getNullCell());
        line.isWrapped = false;
      }
      cachedStart = null;
      return trimmedRows + row;
    }

    function reset() {
      generation++;
      if (workTimer !== null) clearTimeout(workTimer);
      workTimer = null;
      trimmedRows = 0;
      anchoredColumns = terminal.cols;
      busy = false;
      attach();
      notify();
    }

    function rebuild() {
      const token = ++generation;
      if (workTimer !== null) clearTimeout(workTimer);
      workTimer = null;
      attach();
      const count = rawSource.count();
      if (!count) {
        busy = false;
        notify();
        return;
      }
      busy = true;
      notify();
      const buffer = terminal.buffer.active;
      let scanRow = trimmedRows + Math.min(buffer.length - 1, buffer.baseY + buffer.cursorY);
      let nextId = rawSource.at(count - 1).id;
      // An empty cursor after LF is not a tagged line. A tab-only unfinished
      // RX, however, owns its otherwise empty row and must remain navigable.
      const finalRow = buffer.getLine(scanRow - trimmedRows);
      if (!options.isLastLineOpen?.() && finalRow && !finalRow.isWrapped &&
          finalRow.translateToString(true).length === 0) scanRow--;

      function step() {
        workTimer = null;
        if (disposed || token !== generation) return;
        try {
          const deadline = now() + 4;
          let visited = 0;
          let rawIndex = rawSource.findId(nextId);
          if (rawIndex < 0) {
            // Incoming traffic evicted the snapshot tail before it could be
            // indexed. Restart against the new bounded history.
            rebuild();
            return;
          }
          while (scanRow >= trimmedRows && rawIndex >= 0) {
            const row = terminal.buffer.active.getLine(scanRow - trimmedRows);
            if (row && !row.isWrapped) rawSource.setRow(rawIndex--, scanRow);
            scanRow--;
            if (++visited % 256 === 0 && (visited >= 8192 || now() >= deadline)) break;
          }
          // Metadata can outlive physical rows after wrapping or width changes.
          // Mark its unavailable prefix, without another whole-history array.
          if (scanRow < trimmedRows) {
            while (rawIndex >= 0) {
              rawSource.setRow(rawIndex--, -Infinity);
              if (++visited % 256 === 0 && (visited >= 8192 || now() >= deadline)) break;
            }
          }
          if (rawIndex < 0) {
            busy = false;
            notify();
            return;
          }
          nextId = rawSource.at(rawIndex).id;
          workTimer = setTimeout(step, 0);
        } catch (error) {
          // Keep jumps disabled when anchors cannot be trusted.
          busy = true;
          options.onError?.(error);
        }
      }
      workTimer = setTimeout(step, 0);
    }

    function sourceStart() {
      const count = rawSource.count();
      if (busy || count === 0) return count;
      const first = rawSource.at(0), last = rawSource.at(count - 1);
      if (cachedStart && cachedStart.count === count && cachedStart.first === first.id &&
          cachedStart.last === last.id && cachedStart.trimmed === trimmedRows) return cachedStart.value;
      let low = 0, high = count;
      while (low < high) {
        const middle = low + Math.floor((high - low) / 2);
        const row = rawSource.at(middle).row;
        if (!Number.isFinite(row) || row < trimmedRows) low = middle + 1;
        else high = middle;
      }
      cachedStart = { count, first: first.id, last: last.id, trimmed: trimmedRows, value: low };
      return low;
    }

    function indexAtRow(row) {
      const start = sourceStart(), count = rawSource.count();
      if (start >= count) return -1;
      let low = start, high = count - 1, result = start;
      const absolute = row + trimmedRows;
      while (low <= high) {
        const middle = low + Math.floor((high - low) / 2);
        if (rawSource.at(middle).row <= absolute) {
          result = middle;
          low = middle + 1;
        } else high = middle - 1;
      }
      return result - start;
    }

    function currentIndex() {
      return indexAtRow(terminal.buffer.active.viewportY + Math.floor(terminal.rows / 2));
    }

    function viewportRange() {
      const top = terminal.buffer.active.viewportY;
      return { startIndex: indexAtRow(top), endIndex: indexAtRow(top + terminal.rows - 1) };
    }

    function rowAt(index) {
      const start = sourceStart();
      if (!Number.isInteger(index) || index < 0 || start + index >= rawSource.count()) return null;
      const row = rawSource.at(start + index).row - trimmedRows;
      return Number.isFinite(row) && row >= 0 && row < terminal.buffer.active.length ? row : null;
    }

    function indexById(id) {
      const index = rawSource.findId(String(id)) - sourceStart();
      return index >= 0 && rowAt(index) !== null ? index : -1;
    }

    // Includes a soft-wrapped suffix whose original first row was evicted.
    // Undefined means reflow is still rebuilding anchors; null means expired.
    function rowsById(id) {
      if (busy) return undefined;
      const index = rawSource.findId(String(id));
      if (index < 0) return null;
      const start = rawSource.at(index).row - trimmedRows;
      const end = index + 1 < rawSource.count() ? rawSource.at(index + 1).row - trimmedRows
        : terminal.buffer.active.baseY + terminal.buffer.active.cursorY + 1;
      return end > 0 && start < terminal.buffer.active.length
        ? { start: Math.max(0, start), end: Math.min(end, terminal.buffer.active.length), trimmedRows, generation } : null;
    }

    function dispose() {
      disposed = true;
      generation++;
      if (workTimer !== null) clearTimeout(workTimer);
      if (changeTimer !== null) clearTimeout(changeTimer);
      trimSubscription?.dispose();
      resizeSubscription?.dispose();
    }

    attach();
    resizeSubscription = terminal.onResize(() => {
      if (terminal.cols === anchoredColumns) {
        // Height changes move the viewport and trim through the normal ring
        // event, but do not reflow logical lines or invalidate row anchors.
        notify();
        return;
      }
      anchoredColumns = terminal.cols;
      rebuild();
    });
    return { recordRow, reset, sourceStart, currentIndex, viewportRange, rowAt, indexById, rowsById,
      isBusy: () => busy, dispose };
  }

  root.SerialMonitorLogNavigationTerminal = { create };
  if (typeof module === 'object') module.exports = root.SerialMonitorLogNavigationTerminal;
})(globalThis);
