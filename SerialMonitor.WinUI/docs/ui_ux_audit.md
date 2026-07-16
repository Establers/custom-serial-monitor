# UI/UX Audit

Date: 2026-05-25

Scope: static audit of the current WinUI 3 serial monitor UI from `MainWindow.xaml`,
`MainWindow.xaml.cs`, `MainViewModel.cs`, `Assets/xterm`, and `Assets/context`.
No backend changes are recommended here.

## Summary

The app has matured into a capable embedded serial debugging tool: xterm.js is the
right primary log surface, event context is separated into a reliable WebView,
search results are stable by default, and diagnostics/health make long-running
verification practical.

The main UX risk is that the app now exposes many advanced controls in one window.
The main xterm log is still prioritized in the wide layout, but the inspector tab
set, search/marker toolbar, Settings property grid, and bottom TX/saved-command
area are all dense enough that small layout mistakes can quickly feel cramped.
The next UI work should focus on reducing cognitive load and making daily-use
workflows faster, not on adding more features.

## Top 10 UX Issues

1. **Top log toolbar is too crowded**
   - Area: `MainWindow.xaml` search/marker row near the xterm host.
   - The row combines search text, Next/Prev, Case, match summary, matched-line
     preview, rendering state, pause, clear, marker text, and marker action.
   - At medium/narrow widths this is the highest clipping risk and competes with
     the log view's horizontal space.

2. **Inspector has too many equally weighted tabs**
   - Area: `MainWindow.xaml` `TabView` with `Events`, `Context`, `Search`,
     `Rules`, `Settings`, `Diag`, `Help`.
   - All tabs look equally important, but daily-use priority is usually
     `Events`, `Context`, `Search`; `Rules`, `Settings`, `Diag`, and `Help`
     are secondary.
   - The current `TabViewItem` max width helps compactness but can make the
     tab row feel crowded on narrow layouts.

3. **Settings is compact but still visually heavy**
   - Area: `MainWindow.xaml` Settings tab.
   - The aligned `Label | Control | Hint` pattern is good, but the tab contains
     many sections. Users must scroll through serial, log, UI, event context,
     mock stress, and profile settings in one long surface.
   - Apply hints are useful but visually repetitive.

4. **Rules table is functional but has cramped columns**
   - Area: `MainWindow.xaml` Rules tab event/highlight tables.
   - Fixed widths keep alignment stable, but names/keywords/directions can be
     truncated quickly. Highlight rules especially have many columns in a narrow
     inspector.
   - `Edit`/`Del` text buttons are readable but consume more horizontal space
     than icon-style actions would.

5. **Bottom TX and Saved rows consume permanent vertical space**
   - Area: `MainWindow.xaml` root rows 2 and 3.
   - TX and saved-command management are always visible. This is good for shell
     work but costs vertical log space when the user is only observing.
   - Saved command management controls and quick-send chips share one row, which
     can become visually busy.

6. **Connection bar still carries verbose status**
   - Area: `MainWindow.xaml` top connection row.
   - The compact connection status can include port, baud, and status text in
     one long `TextBlock`. It truncates, but users may not know where full status
     lives unless they open Diagnostics.

7. **Diagnostics is powerful but too verbose for routine use**
   - Area: `MainViewModel.BuildDiagnosticsText`, `MainWindow.xaml` Diag tab.
   - Copy Diagnostics is useful, but the diagnostics text is very long. This is
     appropriate for support, not for routine triage.
   - Health summary helps, but diagnostics would benefit from a short top
     summary before the full dump.

8. **Search results are split between two mental models**
   - Area: quick search row, Search tab, xterm search bridge.
   - The quick row jumps in xterm; the Search tab lists C# visible-buffer
     results. This is correct technically, but users must understand that search
     is not full-file search and that results can be stale.
   - The Help tab now explains it, but the Search tab itself should stay explicit.

9. **Keyboard discoverability is weak outside Help**
   - Area: `MainWindow.xaml.cs` root/xterm shortcut handling, saved-command
     editor.
   - Ctrl+C in xterm/context, Ctrl+M marker, and saved command shortcuts are
     implemented, but discoverability depends mostly on Help/tooltips.
   - The main UI does not show active shortcuts on saved command chips.

10. **Multiple WebView surfaces need careful focus behavior**
    - Areas: `Assets/xterm/index.html`, `Assets/context/index.html`,
      `MainWindow.xaml.cs`.
    - xterm and context WebView copy behavior is a strength, but two WebViews
      mean focus, Ctrl+C, shortcuts, and resizing can regress easily.
    - This should be protected with manual verification steps whenever layout or
      WebView bridge code changes.

## Quick Wins

- **Move marker controls out of the main search row or collapse them**
  - Keep a compact `Mark` button near the toolbar.
  - Put marker text in a small flyout or reuse the current inline field only when
    the user expands marker entry.
  - File/area: `MainWindow.xaml` log toolbar.

- **Shorten the search toolbar labels further**
  - Use `N`, `P`, `Aa`, pause icon, clear icon, and tooltips if usability tests
    confirm users understand them.
  - Preserve the text labels if icon-only feels too cryptic.

- **Add shortcut text to saved command tooltips**
  - Example: `Send command: reboot (Ctrl+1)`.
  - File/area: `TxCommand.SendToolTip` or button tooltip binding.

- **Make Diagnostics start with a compact summary**
  - Add health, last error, current serial log path, current event log path,
    file writer running, event detector running, and mock missing count before
    the detailed counters.
  - File/area: `MainViewModel.BuildDiagnosticsText`.

- **Normalize action button labels**
  - `Open S`, `Open E`, `Copy S`, `Copy E` are compact but cryptic.
  - Consider `Serial`, `Events`, `Folder`, `Copy S`, `Copy E`, or icon+tooltip.
  - File/area: Settings > Log.

- **Add tooltips to Settings apply badges consistently**
  - Most badges already have helpful tooltips. Ensure every `Now`, `Reconn`,
    `Restart`, `Profile`, and `Info` badge has one.
  - File/area: Settings tab.

- **Make Help easier to scan**
  - The current Help is useful, but a text-only `TextBox` makes headings less
    visible. Small bold headings in XAML or a WebView/preformatted HTML help
    surface would improve readability without changing backend behavior.

## Medium-Risk Fixes

- **Split Settings into sub-tabs or an internal selector**
  - Candidate sections: `Serial`, `Logs`, `UI`, `Stress`, `Profile`.
  - This reduces scrolling but adds another navigation layer.
  - Risk: tab nesting can feel heavy inside the inspector.

- **Make the TX/Saved area collapsible**
  - Provide `TX` expanded by default, with saved command management collapsible.
  - Risk: hidden TX controls can slow down shell-heavy workflows.

- **Turn Rules into a more table-like control**
  - A DataGrid-like control would improve resizing, selection, sorting, and
    column sizing.
  - Risk: WinUI DataGrid dependencies and styling may introduce performance and
    package complexity.

- **Responsive inspector as SplitView**
  - Wide layout: inspector docked right.
  - Narrow layout: inspector as a bottom/side pane that can hide.
  - Risk: more focus and resizing edge cases with WebView2/xterm fit.

- **Add a dedicated compact status strip inside Diagnostics**
  - Keep the full diagnostics text, but add live summary fields above it.
  - Risk: duplicates bottom health/status data unless carefully scoped.

- **Search result jump robustness**
  - Current indexed jump depends on visible-buffer order and xterm search
    behavior. It is acceptable now, but large logs and trimmed buffers can make
    result indexing confusing.
  - A future xterm marker/decoration strategy may be more robust.

## Do-Not-Change Warnings

- **Do not replace xterm.js with a WinUI TextBox/ListView log view**
  - xterm is solving selection, copy, append smoothness, and future highlighting.
  - Files: `Assets/xterm/index.html`, `MainWindow.xaml.cs`.

- **Do not reintroduce per-line UI log controls for the main log**
  - It risks flicker, poor multi-line selection, and high UI memory cost.

- **Do not update UI controls from serial, file writer, or event detector tasks**
  - Preserve dispatcher marshaling and channel boundaries.
  - Files: `MainViewModel.cs`, service status handlers.

- **Do not auto-refresh search results**
  - Manual refresh preserves selection stability during live logging.

- **Do not write ANSI color codes to serial or event logs**
  - Highlighting must stay visual-only in `LogViewModel`/xterm path.

- **Do not make visible log trimming a health error**
  - Bounded visible UI memory is an intentional architecture rule.

- **Do not apply unsafe serial settings live while connected**
  - Keep reconnect-required behavior for port, baud, data bits, parity, stop
    bits, handshake, DTR, RTS, RX ending, and encoding.

- **Do not hide diagnostics entirely**
  - The app is intended for long-running embedded debugging; copyable diagnostics
    are part of the stability story.

## Suggested Final Layout Direction

### Wide Layout

- Top: compact connection/status strip.
- Center left: xterm log view dominates the window.
- Above xterm: one compact log toolbar with search as the primary control.
- Center right: inspector tabs, but prioritize order:
  - `Events`
  - `Context`
  - `Search`
  - `Rules`
  - `Settings`
  - `Diag`
  - `Help`
- Bottom: compact TX row, with saved command quick chips visible.
- Saved command management can be visually secondary or collapsible.
- Footer: one-line health summary only.

### Medium Layout

- Keep xterm large.
- Keep inspector right-side if width allows at least roughly 360-390 px.
- Reduce secondary toolbar content:
  - collapse marker text,
  - truncate matched-line preview,
  - rely on Search tab for detailed results.

### Narrow Layout

- Stack:
  - connection row,
  - log toolbar,
  - xterm,
  - inspector tabs,
  - TX,
  - saved commands,
  - status.
- Inspector should scroll internally.
- Consider a show/hide inspector toggle if xterm becomes too short.

## Prioritized Implementation Plan

### P0: Preserve Stability

1. Keep xterm/context WebView rendering as-is.
2. Keep search results manual-refresh by default.
3. Keep serial/log/event/settings apply boundaries unchanged.
4. Maintain build verification after every UI pass.

### P1: Daily-Use Compactness

1. Reduce top log toolbar crowding.
   - Candidate: collapse marker text into a flyout.
   - Candidate: move matched-line preview into Search tab only.
2. Normalize compact action labels/tooltips in Settings > Log and Diagnostics.
3. Show saved command shortcuts in chip tooltips.
4. Add a concise Diagnostics summary above the full text dump.

### P2: Inspector Workflow

1. Evaluate whether `Help` should stay as the last tab or move behind `Diag`.
2. Improve Help readability with bold headings or a local HTML/pre viewer.
3. Consider grouping `Rules`, `Settings`, `Diag`, and `Help` as secondary tabs
   if the tab row becomes too crowded.
4. Consider a sticky context header that remains visible while context scrolls.

### P3: Settings And Rules Refinement

1. Add missing tooltips to every Settings apply badge.
2. Consider internal Settings sub-tabs if the single scrolling panel remains
   too long.
3. Improve Rules table column sizing for longer names/keywords.
4. Consider icon-style Edit/Delete actions with consistent tooltips.

### P4: Responsive Layout Hardening

1. Verify 1600 px, 1200 px, and 900 px widths.
2. Verify half-height windows with Rules, Settings, Context, and Diagnostics.
3. Confirm xterm fit after tab switching, window resize, and inspector reflow.
4. Confirm context WebView updates immediately after repeated double-clicks.

## Area Notes

### Overall Layout

- `MainWindow.xaml` uses a good high-level structure: top connection row,
  center log/inspector region, TX row, saved commands row, footer status.
- Wide layout gives xterm a `3*` column and inspector a fixed `390` px width.
  This is a reasonable default for desktop debugging.
- Narrow layout stacks inspector below xterm. This preserves access but can make
  the vertical stack feel tall because TX, saved commands, and footer remain
  always visible.

### Visual Hierarchy

- xterm is visually dominant and should stay that way.
- The top toolbar has too many peers, so search, rendering, and marker actions
  compete for attention.
- Events/Context/Search tabs are daily-use; Settings/Diag/Help are secondary.
  The current tab row does not express this priority.

### Compactness And Consistency

- Global 22 px control height and 12 px font are appropriate for an engineering
  tool.
- Settings property rows are now much more consistent.
- Rules tables remain the most horizontally constrained area.
- Bottom saved command quick chips are compact, but the management combo/actions
  can feel visually mixed with quick-send actions.

### Main Log Workflow

- xterm append, selection, Ctrl+C, and visual-only coloring are the right core UX.
- Search and marker controls are both useful, but both in the same row creates
  congestion.
- Pause and Clear are compact, but `||`, `>`, and `Clr` depend on tooltips.

### Events And Context Workflow

- Events list uses one-line rows, stable incremental updates, Latest, and Auto.
  This is the correct behavior for live logs.
- Double-click to Context is efficient.
- Context WebView is a good reliability choice; keep it.
- Context header is compact. It may still truncate important metadata on narrow
  inspector widths, but the body is prioritized correctly.

### Rules Workflow

- Event and highlight rules are editable and aligned.
- Fixed-width columns prevent layout thrash but truncate real-world keywords.
- Add/Edit/Delete flows are discoverable enough because text buttons are used,
  though they are less compact than icon buttons.

### Settings Workflow

- Settings are grouped sensibly.
- Apply hints are useful and now aligned.
- Validation/status is visible through status/diagnostics; inline field-level
  error decoration is not present.
- Save directory validation is important because log writing depends on it.

### Diagnostics And Health

- Bottom health summary is valuable and should stay compact.
- Diagnostics text is comprehensive but not optimized for quick visual triage.
- Copy Diagnostics is easy to find.
- Log file quick actions are now in Settings > Log; this is coherent, but users
  may still expect them in Diagnostics. Help should mention the Settings location.

### TX And Saved Commands

- Manual TX row is clear and compact.
- Saved command row separates management and quick-send with a divider, which is
  good.
- Shortcut behavior is documented in Help, but chip-level discoverability can
  improve.

### Accessibility And Usability

- Tooltips are widely used and important due to compact labels.
- Ctrl+C behavior in xterm and context is explicitly bridged.
- Shortcut handling avoids text inputs and WebView sources from the WinUI root.
- WebView focus behavior should remain part of manual regression tests.

## Verification Checklist For Future UI Passes

- Connect to `MOCK`; confirm xterm appends and remains selectable.
- Drag-select xterm lines and press Ctrl+C.
- Search `WARN`; use Next/Prev and Search tab result double-click.
- Select/double-click events and verify Context appears immediately.
- Toggle Event Auto and Latest.
- Edit highlight rule color and verify new matching lines update.
- Change Settings values and verify apply status/validation.
- Insert marker through button and Ctrl+M.
- Send manual TX and saved command.
- Open/copy serial and event log paths.
- Run mock stress, pause rendering, and confirm file/event counters continue.
- Resize to wide, medium, and narrow layouts.
