/* Retained-log navigation. This module reads metadata only, never terminal text. */
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.SerialMonitorLogNavigation = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const BLOCK_SIZE = 256;
  const MAX_BINS = 2048;
  const validTime = value => Number.isFinite(value) && Number.isSafeInteger(value);
  const compareIds = (a, b) => a.length === b.length ? (a < b ? -1 : a > b ? 1 : 0) : a.length - b.length;
  const sameDay = (a, b) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  const fail = (error, extra) => ({ ok: false, error, ...extra });

  function create(source) {
    if (!source || typeof source.count !== 'function' || typeof source.at !== 'function') throw new TypeError('A retained metadata source is required.');
    let blocks = [];
    let blockKey = '';
    let lastId = null;
    let lastAbsoluteIndex = -1;
    let baseAbsoluteIndex = 0;
    let histogramCache = null;

    const count = () => Math.max(0, Math.floor(Number(source.count()) || 0));
    const at = index => source.at(index) || { id: '', timestamp: null };
    const time = index => at(index).timestamp;
    const ordered = () => typeof source.timeOrder === 'function' && source.timeOrder() === 'ascending';
    const key = size => typeof source.revision === 'function'
      ? `${source.revision()}:${size}`
      : `${size}:${size ? String(at(0).id) : ''}:${size ? String(at(size - 1).id) : ''}`;

    function lowerBound(target, low, high) {
      while (low < high) {
        const middle = low + Math.floor((high - low) / 2);
        if (time(middle) < target) low = middle + 1;
        else high = middle;
      }
      return low;
    }

    function upperBound(target, low, high) {
      while (low < high) {
        const middle = low + Math.floor((high - low) / 2);
        if (time(middle) <= target) low = middle + 1;
        else high = middle;
      }
      return low;
    }

    function findId(id, size) {
      let low = 0;
      let high = size;
      while (low < high) {
        const middle = low + Math.floor((high - low) / 2);
        const comparison = compareIds(String(at(middle).id), id);
        if (comparison < 0) low = middle + 1;
        else high = middle;
      }
      return low < size && String(at(low).id) === id ? low : -1;
    }

    function indexById(id) {
      if (id === null || id === undefined) return -1;
      const value = String(id);
      return /^\d+$/.test(value) ? findId(value, count()) : -1;
    }

    function summarize(start, end) {
      const summary = { start, end, min: Infinity, max: -Infinity, valid: 0, ordered: true };
      let previous = -Infinity;
      for (let absolute = start; absolute <= end; absolute++) {
        const timestamp = time(absolute - baseAbsoluteIndex);
        if (!validTime(timestamp)) { summary.ordered = false; continue; }
        summary.min = Math.min(summary.min, timestamp);
        summary.max = Math.max(summary.max, timestamp);
        summary.valid++;
        if (timestamp < previous) summary.ordered = false;
        previous = timestamp;
      }
      return summary;
    }

    // Only clock-disordered sources need block summaries. They are bounded by
    // retained capacity and updated for appended/evicted blocks, not re-scanned
    // on every graph paint. The ordinary ordered path uses binary searches.
    function syncBlocks(size) {
      const nextKey = key(size);
      if (blockKey === nextKey) return;
      histogramCache = null;
      let oldLastPosition = -1;
      if (size && lastId !== null) oldLastPosition = findId(lastId, size);
      if (oldLastPosition < 0) { blocks = []; baseAbsoluteIndex = 0; }
      else baseAbsoluteIndex = lastAbsoluteIndex - oldLastPosition;
      const endAbsolute = baseAbsoluteIndex + size - 1;
      const retained = new Map();
      for (const block of blocks) {
        if (block.end >= baseAbsoluteIndex && block.start <= endAbsolute) retained.set(Math.floor(block.start / BLOCK_SIZE), block);
      }
      const nextBlocks = [];
      for (let first = baseAbsoluteIndex; first <= endAbsolute;) {
        const blockNumber = Math.floor(first / BLOCK_SIZE);
        const end = Math.min(endAbsolute, (blockNumber + 1) * BLOCK_SIZE - 1);
        const previous = retained.get(blockNumber);
        nextBlocks.push(previous && previous.start === first && previous.end === end ? previous : summarize(first, end));
        first = end + 1;
      }
      blocks = nextBlocks;
      lastId = size ? String(at(size - 1).id) : null;
      lastAbsoluteIndex = endAbsolute;
      blockKey = nextKey;
    }

    function range() {
      const size = count();
      if (!size) return { count: 0, startTime: null, endTime: null, ordered: ordered(), validCount: 0 };
      if (ordered()) return { count: size, startTime: time(0), endTime: time(size - 1), ordered: true, validCount: size };
      syncBlocks(size);
      let startTime = Infinity;
      let endTime = -Infinity;
      let validCount = 0;
      for (const block of blocks) { startTime = Math.min(startTime, block.min); endTime = Math.max(endTime, block.max); validCount += block.valid; }
      return { count: size, startTime: validCount ? startTime : null, endTime: validCount ? endTime : null, ordered: false, validCount };
    }

    function indexAtTime(timestamp) {
      const size = count();
      // A pixel on the graph can represent a fractional millisecond. Keep
      // stored stamps integral, while allowing finite query boundaries.
      if (!size || !Number.isFinite(timestamp) || Math.abs(timestamp) > Number.MAX_SAFE_INTEGER) return -1;
      if (ordered()) {
        const result = lowerBound(timestamp, 0, size);
        return result < size ? result : -1;
      }
      syncBlocks(size);
      for (const block of blocks) {
        if (block.max < timestamp) continue;
        const first = block.start - baseAbsoluteIndex;
        const end = block.end - baseAbsoluteIndex + 1;
        if (block.ordered) return lowerBound(timestamp, first, end);
        for (let index = first; index < end; index++) {
          const candidate = time(index);
          if (validTime(candidate) && candidate >= timestamp) return index;
        }
      }
      return -1;
    }

    function resolve(input, currentIndex) {
      const value = String(input ?? '').trim();
      if (!value) return fail('empty-input');
      const size = count();
      if (!size) return fail('no-logs');
      const success = index => ({ ok: true, index, id: String(at(index).id), timestamp: time(index) });
      if (value.startsWith('#')) {
        if (!/^#\d+$/.test(value)) return fail('invalid-line');
        const line = Number(value.slice(1));
        return Number.isSafeInteger(line) && line >= 1 && line <= size ? success(line - 1) : fail('out-of-range');
      }
      if (value.endsWith('%')) {
        if (!/^\d+(?:\.\d+)?%$/.test(value)) return fail('invalid-percent');
        const percent = Number(value.slice(0, -1));
        return percent >= 0 && percent <= 100 ? success(Math.round((size - 1) * percent / 100)) : fail('out-of-range');
      }

      let timestamp;
      let absolutePrecision = 0;
      const relative = /^([+-])\s*(\d+(?:\.\d+)?)\s*(ms|s|m|h|d)$/i.exec(value);
      if (relative) {
        if (!Number.isInteger(currentIndex) || currentIndex < 0 || currentIndex >= size || !validTime(time(currentIndex))) return fail('no-current-time');
        const units = { ms: 1, s: 1000, m: 60000, h: 3600000, d: 86400000 };
        timestamp = Math.round(time(currentIndex) + (relative[1] === '-' ? -1 : 1) * Number(relative[2]) * units[relative[3].toLowerCase()]);
      } else {
        const parsed = /^(?:(\d{4})-(\d{2})-(\d{2})[ T])?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?$/.exec(value);
        if (!parsed) return fail('invalid-input');
        const available = range();
        if (!available.validCount) return fail('no-timestamps');
        const firstDate = new Date(available.startTime);
        const lastDate = new Date(available.endTime);
        if (!parsed[1] && !sameDay(firstDate, lastDate)) return fail('ambiguous-time', { ambiguous: true });
        const year = parsed[1] ? Number(parsed[1]) : firstDate.getFullYear();
        const month = parsed[2] ? Number(parsed[2]) - 1 : firstDate.getMonth();
        const day = parsed[3] ? Number(parsed[3]) : firstDate.getDate();
        const hour = Number(parsed[4]);
        const minute = Number(parsed[5]);
        const second = Number(parsed[6] || 0);
        const millisecond = Number((parsed[7] || '').padEnd(3, '0'));
        const date = new Date(0);
        date.setFullYear(year, month, day);
        date.setHours(hour, minute, second, millisecond);
        if (date.getFullYear() !== year || date.getMonth() !== month || date.getDate() !== day || date.getHours() !== hour || date.getMinutes() !== minute || date.getSeconds() !== second || date.getMilliseconds() !== millisecond) return fail('invalid-date');
        timestamp = date.getTime();
        absolutePrecision = parsed[7] !== undefined ? 1 : parsed[6] !== undefined ? 1000 : 60000;
      }
      const available = range();
      if (!available.validCount) return fail('no-timestamps');
      // Users may enter the oldest displayed second/minute without its hidden
      // fractional part. Accept that containing interval, while explicit
      // milliseconds and relative offsets keep their exact boundary behavior.
      const containsOldest = absolutePrecision > 1 && timestamp < available.startTime &&
        available.startTime - timestamp < absolutePrecision;
      if (!validTime(timestamp) || (timestamp < available.startTime && !containsOldest) || timestamp > available.endTime) return fail('out-of-range');
      const index = indexAtTime(timestamp);
      return index >= 0 ? success(index) : fail('out-of-range');
    }

    function histogram(binCount, startIndex, endIndex) {
      const size = count();
      const binSize = Math.max(1, Math.min(MAX_BINS, Math.floor(Number(binCount) || 1)));
      const start = Math.max(0, Math.min(size - 1, Number.isInteger(startIndex) ? startIndex : 0));
      const end = Math.max(start, Math.min(size - 1, Number.isInteger(endIndex) ? endIndex : size - 1));
      const cacheKey = `${key(size)}:${binSize}:${start}:${end}:${ordered()}`;
      if (histogramCache && histogramCache.cacheKey === cacheKey) return histogramCache.value;
      const bins = new Array(binSize).fill(0);
      let startTime = null;
      let endTime = null;
      let validCount = 0;
      if (size) {
        if (ordered()) { startTime = time(start); endTime = time(end); validCount = end - start + 1; }
        else {
          syncBlocks(size);
          let min = Infinity;
          let max = -Infinity;
          for (const block of blocks) {
            const first = Math.max(start, block.start - baseAbsoluteIndex);
            const last = Math.min(end, block.end - baseAbsoluteIndex);
            if (last < first) continue;
            if (first === block.start - baseAbsoluteIndex && last === block.end - baseAbsoluteIndex) {
              min = Math.min(min, block.min); max = Math.max(max, block.max); validCount += block.valid;
            } else for (let index = first; index <= last; index++) {
              const timestamp = time(index);
              if (validTime(timestamp)) { min = Math.min(min, timestamp); max = Math.max(max, timestamp); validCount++; }
            }
          }
          if (validCount) { startTime = min; endTime = max; }
        }
      }
      const span = validCount ? Math.max(1, endTime - startTime + 1) : 1;
      const bucket = timestamp => Math.min(binSize - 1, Math.max(0, Math.floor((timestamp - startTime) / span * binSize)));
      if (validCount && ordered()) {
        let previous = start;
        for (let bin = 0; bin < binSize; bin++) {
          const next = bin === binSize - 1 ? end + 1 : lowerBound(startTime + span * (bin + 1) / binSize, previous, end + 1);
          bins[bin] = next - previous;
          previous = next;
        }
      } else if (validCount) {
        for (const block of blocks) {
          const first = Math.max(start, block.start - baseAbsoluteIndex);
          const last = Math.min(end, block.end - baseAbsoluteIndex);
          if (last < first || !block.valid) continue;
          if (first === block.start - baseAbsoluteIndex && last === block.end - baseAbsoluteIndex && bucket(block.min) === bucket(block.max)) bins[bucket(block.min)] += block.valid;
          else if (block.ordered) {
            const firstBin = bucket(time(first));
            const lastBin = bucket(time(last));
            let previous = first;
            for (let bin = firstBin; bin <= lastBin; bin++) {
              const next = bin === lastBin ? last + 1 : lowerBound(startTime + span * (bin + 1) / binSize, previous, last + 1);
              bins[bin] += next - previous;
              previous = next;
            }
          } else for (let index = first; index <= last; index++) { const timestamp = time(index); if (validTime(timestamp)) bins[bucket(timestamp)]++; }
        }
      }
      const result = { bins, startTime, endTime, peak: Math.max(...bins), count: validCount, binMilliseconds: span / binSize, ordered: ordered(), startIndex: start, endIndex: end };
      histogramCache = { cacheKey, value: result };
      return result;
    }

    function histogramRange(binCount, requestedStart, requestedEnd) {
      const available = range();
      if (!available.validCount || !Number.isFinite(requestedStart) || !Number.isFinite(requestedEnd) ||
          requestedEnd < requestedStart || requestedStart > available.endTime || requestedEnd < available.startTime) {
        return histogram(binCount);
      }
      const startTime = Math.max(available.startTime, requestedStart);
      const endTime = Math.min(available.endTime, requestedEnd);
      const size = available.count;
      const binSize = Math.max(1, Math.min(MAX_BINS, Math.floor(Number(binCount) || 1)));
      const cacheKey = `time:${key(size)}:${binSize}:${startTime}:${endTime}:${ordered()}`;
      if (histogramCache && histogramCache.cacheKey === cacheKey) return histogramCache.value;
      const bins = new Array(binSize).fill(0);
      const span = Math.max(1, endTime - startTime + 1);
      const bucket = timestamp => Math.min(binSize - 1, Math.max(0, Math.floor((timestamp - startTime) / span * binSize)));
      let validCount = 0;
      let startIndex = -1;
      let endIndex = -1;

      function includeOrdered(first, end) {
        if (first >= end) return;
        validCount += end - first;
        if (startIndex < 0) startIndex = first;
        endIndex = end - 1;
        const firstBin = bucket(time(first));
        const lastBin = bucket(time(end - 1));
        let previous = first;
        for (let bin = firstBin; bin <= lastBin; bin++) {
          const next = bin === lastBin ? end : lowerBound(startTime + span * (bin + 1) / binSize, previous, end);
          bins[bin] += next - previous;
          previous = next;
        }
      }

      if (ordered()) {
        const first = lowerBound(startTime, 0, size);
        includeOrdered(first, upperBound(endTime, first, size));
      } else {
        syncBlocks(size);
        for (const block of blocks) {
          if (!block.valid || block.max < startTime || block.min > endTime) continue;
          const first = block.start - baseAbsoluteIndex;
          const end = block.end - baseAbsoluteIndex + 1;
          if (block.ordered) {
            const selectedFirst = lowerBound(startTime, first, end);
            includeOrdered(selectedFirst, upperBound(endTime, selectedFirst, end));
          } else if (block.min >= startTime && block.max <= endTime && bucket(block.min) === bucket(block.max)) {
            bins[bucket(block.min)] += block.valid;
            validCount += block.valid;
            // Null metadata can occur at block edges, so find real endpoints.
            let selectedFirst = first;
            while (selectedFirst < end && !validTime(time(selectedFirst))) selectedFirst++;
            let selectedEnd = end - 1;
            while (selectedEnd >= first && !validTime(time(selectedEnd))) selectedEnd--;
            if (startIndex < 0) startIndex = selectedFirst;
            endIndex = selectedEnd;
          } else {
            for (let index = first; index < end; index++) {
              const timestamp = time(index);
              if (!validTime(timestamp) || timestamp < startTime || timestamp > endTime) continue;
              bins[bucket(timestamp)]++;
              validCount++;
              if (startIndex < 0) startIndex = index;
              endIndex = index;
            }
          }
        }
      }
      const result = { bins, startTime, endTime, peak: Math.max(...bins), count: validCount,
        binMilliseconds: span / binSize, ordered: ordered(), startIndex, endIndex };
      histogramCache = { cacheKey, value: result };
      return result;
    }

    return { resolve, range, indexAtTime, indexById, histogram, histogramRange };
  }

  return { create };
});
