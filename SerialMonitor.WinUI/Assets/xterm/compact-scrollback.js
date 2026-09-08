/* Lossless cold-row storage for the bundled xterm BufferLine layout.
 * Only historical rows are replaced; the parser's live screen stays native.
 * Keep the differential tests against xterm.js when updating that dependency.
 */
(function (root) {
  'use strict';

  // Encode the three uint32 cell planes independently. XOR deltas make ASCII
  // content small; runs compact repeated colors and trailing empty cells.
  function pack(data) {
    const bytes = [];
    function uint(value) {
      value >>>= 0;
      while (value >= 128) {
        bytes.push((value & 127) | 128);
        value >>>= 7;
      }
      bytes.push(value);
    }
    uint(data.length);
    for (let plane = 0; plane < 3; plane++) {
      let previous = 0;
      for (let i = plane; i < data.length;) {
        let end = i + 3;
        while (end < data.length && data[end] === data[i]) end += 3;
        const count = (end - i) / 3;
        if (count >= 3) {
          uint(count * 2 + 1);
          uint(data[i] ^ previous);
          previous = data[i];
          i = end;
        } else {
          end = i;
          do {
            end += 3;
          } while (end < data.length && !(end + 6 < data.length &&
            data[end] === data[end + 3] && data[end] === data[end + 6]));
          uint(((end - i) / 3) * 2);
          for (; i < end; i += 3) {
            uint(data[i] ^ previous);
            previous = data[i];
          }
        }
      }
    }
    // High-entropy cells must never expand beyond their original byte size
    // (apart from a one-byte encoding tag).
    if (bytes.length >= data.byteLength) {
      const result = new Uint8Array(data.byteLength + 1);
      result[0] = 0;
      result.set(new Uint8Array(data.buffer, data.byteOffset, data.byteLength), 1);
      return result;
    }
    const result = new Uint8Array(bytes.length + 1);
    result[0] = 1;
    result.set(bytes, 1);
    return result;
  }

  function unpack(bytes) {
    if (bytes[0] === 0) return new Uint32Array(bytes.slice(1).buffer);
    let offset = 1;
    function uint() {
      let result = 0, shift = 0, byte;
      do {
        byte = bytes[offset++];
        result |= (byte & 127) << shift;
        shift += 7;
      } while (byte & 128);
      return result >>> 0;
    }
    const data = new Uint32Array(uint());
    for (let plane = 0; plane < 3; plane++) {
      let previous = 0;
      for (let i = plane; i < data.length;) {
        const token = uint(), count = token >>> 1;
        if (token & 1) {
          const value = (uint() ^ previous) >>> 0;
          for (let n = 0; n < count; n++, i += 3) data[i] = value;
          previous = value;
        } else {
          for (let n = 0; n < count; n++, i += 3) {
            previous = (uint() ^ previous) >>> 0;
            data[i] = previous;
          }
        }
      }
    }
    return data;
  }

  function install(terminal, { hotRows = 256, cacheRows = 512, onError = console.error } = {}) {
    if (!Number.isInteger(hotRows) || hotRows < 0 || !Number.isInteger(cacheRows) || cacheRows < 2) {
      throw new Error('Invalid compact scrollback cache limits');
    }
    const buffers = terminal._core?._bufferService?.buffers;
    const sample = buffers?.normal?.lines?.get(0);
    if (!sample || !(sample._data instanceof Uint32Array) || sample._data.length !== sample.length * 3) {
      throw new Error('Unsupported xterm cell layout for compact scrollback');
    }
    const nativePrototype = Object.getPrototypeOf(sample);
    const cache = new Set();
    let disposed = false;
    let failed = false;
    let timer = null;
    let trackedLines = null;
    let scanned = 0;
    let subscriptions = [];

    function remember(line) {
      cache.add(line);
      if (cache.size > cacheRows) {
        const oldest = cache.values().next().value;
        // _data is mutable. Re-encode on eviction, never reuse an old encoding.
        oldest._packedCells = pack(oldest._denseCells);
        oldest._denseCells = null;
        if (oldest._combinedCells && Object.keys(oldest._combinedCells).length === 0) oldest._combinedCells = null;
        if (oldest._extendedCells && Object.keys(oldest._extendedCells).length === 0) oldest._extendedCells = null;
        cache.delete(oldest);
      }
    }

    const compactPrototype = Object.create(nativePrototype, {
      _combined: {
        get() { return this._combinedCells ??= {}; },
        set(value) { this._combinedCells = value; }
      },
      _extendedAttrs: {
        get() { return this._extendedCells ??= {}; },
        set(value) { this._extendedCells = value; }
      },
      _data: {
        get() {
          if (!this._denseCells) {
            this._denseCells = unpack(this._packedCells);
            this._packedCells = null;
            remember(this);
          }
          return this._denseCells;
        },
        set(value) {
          this._denseCells = value;
          this._packedCells = null;
          remember(this);
        }
      },
      resize: { value(cols, fillCell) {
        // A height-only resize must not inflate the entire history.
        if (cols === this.length && !this._denseCells) return false;
        return nativePrototype.resize.call(this, cols, fillCell);
      } },
      cleanupMemory: { value() {
        return this._denseCells ? nativePrototype.cleanupMemory.call(this) : 0;
      } }
    });

    function archive(line) {
      const result = Object.create(compactPrototype);
      result.isWrapped = line.isWrapped;
      result._combinedCells = Object.keys(line._combined).length ? line._combined : null;
      result._extendedCells = Object.keys(line._extendedAttrs).length ? line._extendedAttrs : null;
      result.length = line.length;
      result._denseCells = null;
      result._packedCells = pack(line._data);
      return result;
    }

    function track(lines) {
      for (const subscription of subscriptions) subscription.dispose();
      trackedLines = lines;
      scanned = 0;
      subscriptions = [
        lines.onTrim(count => { scanned = Math.max(0, scanned - count); }),
        lines.onInsert(() => { scanned = 0; }),
        lines.onDelete(() => { scanned = 0; })
      ];
    }

    function compact(maxRows = 2048, timeBudgetMs = 4) {
      if (disposed || failed) return;
      try {
        compactRows(maxRows, timeBudgetMs);
      } catch (error) {
        failed = true;
        if (timer !== null) { clearTimeout(timer); timer = null; }
        onError(error);
      }
    }

    function compactRows(maxRows, timeBudgetMs) {
      const normal = buffers.normal;
      if (trackedLines !== normal.lines) track(normal.lines);
      const limit = Math.max(0, normal.ybase - hotRows);
      scanned = Math.min(scanned, limit);
      const deadline = performance.now() + timeBudgetMs;
      let visited = 0;
      while (scanned < limit && visited < maxRows) {
        const line = trackedLines.get(scanned);
        if (Object.getPrototypeOf(line) !== compactPrototype) {
          trackedLines.set(scanned, archive(line));
        }
        scanned++;
        if (++visited % 32 === 0 && performance.now() >= deadline) break;
      }
      if (scanned < limit && timer === null) {
        timer = setTimeout(() => { timer = null; compact(); }, 0);
      }
    }

    const resizeSubscription = terminal.onResize(() => { scanned = 0; compact(); });
    return {
      compact,
      dispose() {
        disposed = true;
        if (timer !== null) clearTimeout(timer);
        resizeSubscription.dispose();
        for (const subscription of subscriptions) subscription.dispose();
        // Keep rows readable if the owner disposes the adapter before xterm.
        for (const line of cache) {
          line._packedCells = pack(line._denseCells);
          line._denseCells = null;
        }
        cache.clear();
      }
    };
  }

  root.SerialMonitorCompactScrollback = { install, pack, unpack };
  if (typeof module === 'object') module.exports = root.SerialMonitorCompactScrollback;
})(globalThis);
