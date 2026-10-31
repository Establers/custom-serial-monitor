// Static native UI regressions supplement transport/model tests without requiring
// a desktop session. They protect the removed tab and reserved dialog layout.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const root = path.resolve(__dirname, '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');
const main = read('SerialMonitor.WinUI/MainWindow.xaml');
const host = read('SerialMonitor.WinUI/MainWindow.LogNavigation.cs');
const view = read('SerialMonitor.WinUI/Views/LogNavigationView.xaml');
const viewCode = read('SerialMonitor.WinUI/Views/LogNavigationView.xaml.cs');
function element(name) {
  const found = view.match(new RegExp(`<(?:TextBlock|Border)\\b[^>]*x:Name="${name}"[^>]*>`));
  assert.ok(found, `Missing ${name} status region`);
  return found[0];
}
function attribute(markup, name) {
  return markup.match(new RegExp(`\\b${name}="([^"]*)"`))?.[1];
}

test('navigation is opened on demand and has no inspector tab', () => {
  assert.doesNotMatch(main, /<TabViewItem\b[^>]*(?:NativeLogNavigationTab|NavigationTabViewItem)/);
  assert.doesNotMatch(main, /NavigationTabContent/);
  assert.doesNotMatch(host, /NavigationTabContent|NavigationTabViewItem|IsNavigationTabVisible/);
  assert.match(host, /new LogNavigationDialog\b/);
  assert.match(host, /PrepareForOpenAsync\s*\(/);
  assert.match(main, /<Button\b[^>]*x:Name="LogNavigateButton"[^>]*Click="LogNavigateButton_Click"/);
  assert.match(host, /async void LogNavigateButton_Click\b[\s\S]*?await OpenLogNavigationAsync\(\)/);
});

test('bounded numeric date/time selectors replace the freeform input', () => {
  assert.doesNotMatch(view, /<TextBox\b/);
  for (const part of ['Year', 'Month', 'Day', 'Hour', 'Minute', 'Second']) {
    const picker = view.match(new RegExp(`<views:NumericTimePartPicker\\b[^>]*x:Name="${part}Picker"[^>]*>`))?.[0];
    assert.ok(picker, `Missing numeric ${part} picker`);
    assert.equal(attribute(picker, 'CommittedValue'), `{Binding Selected${part}, Mode=TwoWay}`);
  }
  assert.match(view, /IsEnabled="\{Binding HasRange\}"/);
});

test('range and errors share a fixed status slot without resizing the dialog', () => {
  const rootTag = view.match(/<UserControl\b[^>]*>/)?.[0];
  assert.ok(Number(attribute(rootTag, 'Height')) > 0, 'Dialog content reserves its height');
  const gridTag = view.match(/<Grid\b[^>]*x:Name="NavigationRoot"[^>]*>/)?.[0];
  const rows = attribute(gridTag, 'RowDefinitions').split(',');
  const statusSlot = view.match(/<Grid\b[^>]*Grid.Row="(\d+)"[^>]*Height="(\d+)"[^>]*>([\s\S]*)<\/Grid>\s*<\/Grid>\s*<\/UserControl>/);
  assert.ok(statusSlot, 'Status messages need one reserved slot');
  assert.equal(Number(rows[Number(statusSlot[1])]), Number(statusSlot[2]));
  for (const name of ['ErrorText', 'AvailableRange']) {
    const region = element(name);
    const height = Number(attribute(region, 'Height'));
    assert.ok(Number.isFinite(height) && height > 0, `${name} needs a fixed height`);
    assert.equal(Number(statusSlot[2]), height, `${name} must fit inside the reserved slot`);
    assert.ok(statusSlot[3].includes(region), `${name} must stay inside the shared slot`);
  }
  assert.equal(attribute(element('ErrorText'), 'MaxLines'), '3');
  assert.equal(attribute(element('ErrorText'), 'TextTrimming'), 'CharacterEllipsis');
  assert.match(element('AvailableRange'), /ToolTipService\.ToolTip=/);
  assert.match(viewCode, /ToolTipService.SetToolTip\(ErrorText,/);
  assert.match(viewCode, /AutomationProperties.SetHelpText\(NavigationRoot, range\)/);
  assert.doesNotMatch(view, /<TextBlock\b[^>]*x:Name="(?:ErrorFeedback|ViewError)"/);
});

test('Go and Back are state-bound and the open-time range has no polling timer', () => {
  assert.match(host, /IsPrimaryButtonEnabledProperty/);
  assert.match(host, /IsSecondaryButtonEnabledProperty/);
  assert.match(host, /nameof\(LogTimePickerViewModel\.CanNavigate\)/);
  assert.match(host, /nameof\(LogTimePickerViewModel\.CanGoBack\)/);
  assert.match(host, /SecondaryButtonText\s*=/);
  assert.match(host, /_navigationDialogOpen/);
  assert.doesNotMatch(host, /DispatcherQueueTimer|CreateTimer|OnNavigationRefreshTimer|RefreshAsync/);
});
