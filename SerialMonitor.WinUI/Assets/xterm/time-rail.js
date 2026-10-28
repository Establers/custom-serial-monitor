/* A metadata-only time ruler. No terminal cells or log text are read here. */
(function (global) {
  'use strict';

  function install(options) {
    const doc = global.document;
    const isKorean = String(options.language || doc.documentElement.lang || global.navigator?.language || 'en').startsWith('ko');
    const label = isKorean ? '시간 또는 위치로 이동' : 'Go to time or position';
    const busyText = isKorean ? '로그 화면 갱신 중' : 'Updating log view';
    const unavailableText = isKorean ? '선택한 로그를 찾을 수 없습니다. 다시 선택해 주세요.' : 'This log is unavailable. Choose another position.';
    const expiredText = isKorean ? '선택한 로그가 보관 범위를 벗어났습니다. 다시 선택해 주세요.' : 'This log has left the retained buffer. Choose another position.';
    const noTimeText = isKorean ? '시각 정보 없음' : 'Time unavailable';
    const minimumTimestamp = -62135596800000;
    const maximumTimestamp = 253402300799999;
    let state = { count: 0, startTime: null, endTime: null, busy: false };
    let disposed = false;
    let refreshTimer = 0;
    let previewFrame = 0;
    let lastRefresh = -Infinity;
    let selectedFraction = 0.5;
    let lastWidth = -1;
    let tickCount = -1;
    let previewY = 0;
    let hoverActive = false;
    let keyboardActive = false;
    let pointerPress = null;
    let previewTarget = null;
    const removers = [];

    function create(tag, className, parent) {
      const node = doc.createElement(tag);
      if (className) node.className = className;
      parent.appendChild(node);
      return node;
    }
    function listen(node, event, callback, settings) {
      node.addEventListener(event, callback, settings);
      removers.push(() => node.removeEventListener(event, callback, settings));
    }
    function report(error) {
      if (typeof options.onError === 'function') options.onError(error);
      else global.console?.error('Time rail failed', error);
    }
    function safely(action) {
      try { return action(); } catch (error) { report(error); return null; }
    }
    function hasLogs() {
      return Number.isSafeInteger(state.count) && state.count > 0;
    }
    function validTime(value) {
      return Number.isSafeInteger(value) && value >= minimumTimestamp && value <= maximumTimestamp;
    }
    function hasTime() {
      return hasLogs() && validTime(state.startTime) && validTime(state.endTime) && state.startTime <= state.endTime;
    }
    function usesTime() {
      // Identical timestamps cannot distinguish several logs; position remains
      // useful while their actual shared timestamp is still shown in the hint.
      return hasTime() && (state.startTime < state.endTime || state.count === 1);
    }
    function mode() {
      return !hasLogs() ? 'empty' : usesTime() ? 'time' : 'position';
    }
    function readState() {
      const next = options.getState() || {};
      // Width changes can temporarily rebuild row anchors. Keep the completed
      // ruler visible while disabling jumps until those anchors are ready.
      state = next.busy ? { ...state, busy: true } : next;
      return state;
    }
    function formatTime(value, includeDate = true) {
      const date = new Date(value);
      const two = value => String(value).padStart(2, '0');
      const day = `${date.getFullYear()}-${two(date.getMonth() + 1)}-${two(date.getDate())}`;
      const time = `${two(date.getHours())}:${two(date.getMinutes())}:${two(date.getSeconds())}.${String(date.getMilliseconds()).padStart(3, '0')}`;
      return includeDate ? `${day} ${time}` : time;
    }
    function targetTime(fraction = selectedFraction) {
      if (!hasTime()) return null;
      return Math.ceil(state.startTime + Math.max(0, Math.min(1, fraction)) * (state.endTime - state.startTime));
    }
    function targetIndex(fraction = selectedFraction) {
      return hasLogs() ? Math.round(Math.max(0, Math.min(1, fraction)) * (state.count - 1)) : null;
    }
    function positionText(index, timestamp) {
      const line = Number.isInteger(index) ? Math.max(0, Math.min(state.count - 1, index)) : targetIndex();
      const percent = state.count > 1 ? line / (state.count - 1) * 100 : 0;
      const location = `#${line + 1} / ${state.count} · ${percent.toFixed(1)}%`;
      return validTime(timestamp) ? `${formatTime(timestamp)}\n${location}` : `${location} · ${noTimeText}`;
    }

    const root = create('div', 'time-rail', doc.body);
    root.id = 'timeRail';
    root.tabIndex = 0;
    root.setAttribute('role', 'slider');
    root.setAttribute('aria-label', label);
    root.setAttribute('aria-orientation', 'vertical');
    root.setAttribute('aria-valuemin', '0');
    root.setAttribute('aria-valuemax', '100');
    root.setAttribute('aria-valuenow', '50');
    root.setAttribute('aria-description', isKorean
      ? '마우스를 올리면 시각 또는 위치 표시, 클릭하면 이동. 방향키로 선택 후 Enter로 이동합니다.'
      : 'Hover to see a time or position, click to jump. Arrow keys choose a location; Enter jumps.');
    create('div', 'time-rail-axis', root).setAttribute('aria-hidden', 'true');
    const ticks = create('div', 'time-rail-ticks', root);
    ticks.setAttribute('aria-hidden', 'true');
    const viewport = create('div', 'time-rail-viewport', root);
    viewport.setAttribute('aria-hidden', 'true');
    const cursor = create('div', 'time-rail-cursor', root);
    cursor.hidden = true;
    cursor.setAttribute('aria-hidden', 'true');
    const tooltip = create('div', 'time-rail-tooltip', doc.body);
    tooltip.id = 'timeRailTooltip';
    tooltip.setAttribute('role', 'tooltip');
    tooltip.hidden = true;
    root.setAttribute('aria-describedby', tooltip.id);

    function hidePreview() {
      hoverActive = false;
      keyboardActive = false;
      previewTarget = null;
      tooltip.hidden = true;
      cursor.hidden = true;
      if (previewFrame) global.cancelAnimationFrame(previewFrame);
      previewFrame = 0;
    }
    function positionTooltip() {
      const rect = root.getBoundingClientRect();
      const y = rect.top + selectedFraction * rect.height;
      tooltip.style.left = `${Math.max(2, Math.min(global.innerWidth - tooltip.offsetWidth - 3, rect.right + 5))}px`;
      tooltip.style.top = `${Math.max(3, Math.min(global.innerHeight - tooltip.offsetHeight - 3, y - tooltip.offsetHeight / 2))}px`;
    }
    function showPreview(fraction) {
      if (disposed || !hasLogs()) { hidePreview(); return; }
      selectedFraction = Math.max(0, Math.min(1, fraction));
      const timeMode = usesTime();
      const requested = timeMode ? targetTime() : targetIndex();
      if (requested === null) return;
      cursor.style.top = `${selectedFraction * 100}%`;
      cursor.hidden = false;
      root.setAttribute('aria-valuenow', String(Math.round(selectedFraction * 100)));
      if (state.busy) {
        previewTarget = null;
        tooltip.textContent = busyText;
      } else {
        const resolved = safely(() => timeMode ? options.resolveTime?.(requested) : options.resolveIndex?.(requested));
        previewTarget = resolved?.ok ? { mode: timeMode ? 'time' : 'position', fraction: selectedFraction,
          requested, index: resolved.index ?? requested, id: resolved.id } : null;
        if (resolved?.ok) {
          if (timeMode && validTime(resolved.timestamp)) {
            tooltip.textContent = formatTime(resolved.timestamp);
            if (Math.abs(resolved.timestamp - requested) >= 1000) {
              tooltip.textContent += `\n${isKorean ? '요청' : 'Requested'} ${formatTime(requested, false)}`;
            }
          } else {
            tooltip.textContent = positionText(resolved.index ?? requested, resolved.timestamp);
          }
          root.setAttribute('aria-valuetext', tooltip.textContent);
        } else {
          tooltip.textContent = resolved?.error === 'busy' ? busyText : unavailableText;
          root.setAttribute('aria-valuetext', tooltip.textContent);
        }
      }
      tooltip.hidden = false;
      positionTooltip();
    }
    function queuePreview(y) {
      previewY = y;
      if (previewFrame) return;
      previewFrame = global.requestAnimationFrame(() => {
        previewFrame = 0;
        if (!hoverActive || disposed) return;
        const rect = root.getBoundingClientRect();
        showPreview((previewY - rect.top) / Math.max(1, rect.height));
      });
    }
    function activate(fraction, keyboard) {
      safely(() => {
        const cached = previewTarget;
        readState();
        if (state.busy) {
          previewTarget = null;
          tooltip.textContent = busyText;
          tooltip.hidden = false;
          positionTooltip();
          refresh();
          return;
        }
        if (!hasLogs()) { hidePreview(); return; }
        selectedFraction = Math.max(0, Math.min(1, fraction));
        const timeMode = usesTime();
        const matchesPreview = cached && cached.mode === (timeMode ? 'time' : 'position') &&
          Math.abs(cached.fraction - selectedFraction) < 0.000001;
        const requested = matchesPreview ? cached.requested : timeMode ? targetTime() : targetIndex();
        const result = timeMode ? options.navigateTime?.(requested)
          : options.navigateIndex?.(matchesPreview ? cached.index : requested, matchesPreview ? cached.id : undefined);
        if (!result?.ok) {
          previewTarget = null;
          tooltip.textContent = result?.error === 'busy' ? busyText
            : ['expired', 'history-expired', 'line-out-of-range', 'out-of-range'].includes(result?.error) ? expiredText : unavailableText;
          tooltip.hidden = false;
          positionTooltip();
        } else {
          showPreview(selectedFraction);
        }
        if (keyboard) root.focus({ preventScroll: true });
        refresh();
      });
    }
    function renderTicks() {
      const count = Math.max(10, Math.min(80, Math.floor(root.clientHeight / 13)));
      if (count === tickCount) return;
      tickCount = count;
      ticks.replaceChildren();
      for (let index = 0; index <= count; index++) {
        const tick = create('span', `time-rail-tick${index % 5 === 0 || index === count ? ' is-major' : ''}`, ticks);
        tick.style.top = `${index / count * 100}%`;
      }
    }
    function update() {
      refreshTimer = 0;
      if (disposed) return;
      lastRefresh = global.performance.now();
      safely(() => {
        readState();
        root.classList.toggle('is-empty', !hasLogs());
        root.dataset.mode = mode();
        root.classList.toggle('is-busy', !!state.busy);
        root.setAttribute('aria-disabled', String(!hasLogs() || !!state.busy));
        root.setAttribute('aria-busy', String(!!state.busy));
        const width = root.getBoundingClientRect().width;
        if (Math.abs(width - lastWidth) > 0.1) {
          lastWidth = width;
          options.onLayoutChanged?.(width);
        }
        renderTicks();
        let top = null, bottom = null;
        const first = state.viewportStartTime, last = state.viewportEndTime;
        if (usesTime() && validTime(first) && validTime(last) &&
            Math.max(first, last) >= state.startTime && Math.min(first, last) <= state.endTime) {
          const span = Math.max(1, state.endTime - state.startTime);
          top = Math.max(0, Math.min(1, (Math.min(first, last) - state.startTime) / span));
          bottom = state.count === 1 ? 1 : Math.max(0, Math.min(1, (Math.max(first, last) - state.startTime) / span));
        } else if (hasLogs()) {
          const firstIndex = state.viewportStartIndex ?? state.startIndex ?? state.currentIndex;
          const lastIndex = state.viewportEndIndex ?? state.endIndex ?? state.currentIndex;
          if (Number.isInteger(firstIndex) && firstIndex >= 0 && Number.isInteger(lastIndex) && lastIndex >= 0) {
            const span = Math.max(1, state.count - 1);
            top = Math.max(0, Math.min(1, Math.min(firstIndex, lastIndex) / span));
            bottom = state.count === 1 ? 1 : Math.max(0, Math.min(1, Math.max(firstIndex, lastIndex) / span));
          }
        }
        if (top === null || bottom === null) {
          viewport.hidden = true;
        } else {
          const height = root.clientHeight;
          const topPixels = Math.min(Math.max(0, height - 3), top * height);
          viewport.style.top = `${topPixels}px`;
          viewport.style.height = `${Math.max(3, Math.min(height - topPixels, (bottom - top) * height))}px`;
          viewport.hidden = false;
        }
        if (!hasLogs()) hidePreview();
        else if (keyboardActive || hoverActive) showPreview(selectedFraction);
      });
    }
    function refresh() {
      if (disposed || refreshTimer) return;
      refreshTimer = global.setTimeout(update, Math.max(0, 250 - (global.performance.now() - lastRefresh)));
    }
    function reset() {
      state = { count: 0, startTime: null, endTime: null, busy: false };
      selectedFraction = 0.5;
      root.setAttribute('aria-valuenow', '50');
      root.removeAttribute('aria-valuetext');
      root.setAttribute('aria-disabled', 'true');
      root.setAttribute('aria-busy', 'false');
      root.dataset.mode = 'empty';
      root.classList.add('is-empty');
      root.classList.remove('is-busy');
      pointerPress = null;
      viewport.hidden = true;
      hidePreview();
      refresh();
    }
    function dispose() {
      if (disposed) return;
      disposed = true;
      global.clearTimeout(refreshTimer);
      if (previewFrame) global.cancelAnimationFrame(previewFrame);
      removers.forEach(remove => remove());
      resizeObserver?.disconnect();
      root.remove();
      tooltip.remove();
    }

    listen(root, 'pointermove', event => { hoverActive = true; keyboardActive = false; queuePreview(event.clientY); });
    listen(root, 'pointerleave', () => { if (!keyboardActive) hidePreview(); });
    listen(root, 'pointerdown', event => {
      if (event.button !== 0) return;
      pointerPress = { x: event.clientX, y: event.clientY, id: event.pointerId };
      event.preventDefault();
      root.focus({ preventScroll: true });
      root.setPointerCapture?.(event.pointerId);
    });
    listen(root, 'pointerup', event => {
      const press = pointerPress;
      pointerPress = null;
      if (!press || press.id !== event.pointerId || event.button !== 0) return;
      if (root.hasPointerCapture?.(event.pointerId)) root.releasePointerCapture(event.pointerId);
      if (Math.hypot(event.clientX - press.x, event.clientY - press.y) > 5) return;
      event.preventDefault();
      const rect = root.getBoundingClientRect();
      activate((event.clientY - rect.top) / Math.max(1, rect.height), false);
    });
    listen(root, 'pointercancel', () => { pointerPress = null; hidePreview(); });
    listen(root, 'blur', hidePreview);
    listen(root, 'keydown', event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); hidePreview(); return; }
      if (event.ctrlKey || event.metaKey || event.altKey || !hasLogs()) return;
      const step = event.shiftKey ? 0.1 : 0.01;
      let fraction = selectedFraction;
      if (event.key === 'ArrowUp' || event.key === 'ArrowLeft') fraction -= step;
      else if (event.key === 'ArrowDown' || event.key === 'ArrowRight') fraction += step;
      else if (event.key === 'Home') fraction = 0;
      else if (event.key === 'End') fraction = 1;
      else if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault(); event.stopPropagation();
        if (event.repeat) return;
        keyboardActive = true;
        activate(selectedFraction, true);
        return;
      } else return;
      event.preventDefault(); event.stopPropagation();
      keyboardActive = true;
      hoverActive = false;
      showPreview(fraction);
    });
    listen(global, 'resize', refresh);
    const resizeObserver = global.ResizeObserver ? new ResizeObserver(refresh) : null;
    resizeObserver?.observe(root);
    update();
    return { refresh, reset, dispose,
      getState: () => ({ selectedFraction, tooltipVisible: !tooltip.hidden, width: lastWidth, mode: mode() }) };
  }

  global.SerialMonitorTimeRail = { install };
})(typeof window !== 'undefined' ? window : globalThis);
