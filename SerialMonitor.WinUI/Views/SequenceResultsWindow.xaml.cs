using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class SequenceResultsWindow : Window
{
    private readonly SequenceResultsViewModel _model;
    private readonly string _resultsRootDirectory;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private SequenceLogDisplayOptions _displayOptions = new();
    private bool _loaded;
    public SequenceResultsWindow(SequenceRunStore store, ElementTheme theme)
    {
        InitializeComponent();
        Root.RequestedTheme = theme;
        _resultsRootDirectory = store.Root;
        _model = new(store); Root.DataContext = _model;
        _model.PropertyChanged += ModelChanged;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Tick;
        Root.Loaded += async (_, _) => { _loaded = true; await _model.RefreshRunsAsync(); if (_loaded) _timer.Start(); };
        Activated += async (_, args) =>
        {
            if (_loaded && args.WindowActivationState != WindowActivationState.Deactivated)
                await _model.RefreshRunsAsync();
        };
        Closed += (_, _) => { _loaded = false; _timer.Stop(); _timer.Tick -= Tick; _model.PropertyChanged -= ModelChanged; _model.Dispose(); };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 800));
    }
    private async void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        // New runs must appear even while an older result remains selected.
        // Schedule the next poll after this one finishes, including slow disks.
        _timer.Stop();
        try { await _model.RefreshRunsAsync(); }
        finally { if (_loaded) _timer.Start(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await _model.RefreshRunsAsync();
    private async void Run_Changed(object sender, SelectionChangedEventArgs args) { if (_loaded) await _model.RefreshAsync(); }
    private async void Step_Changed(object sender, SelectionChangedEventArgs args) { if (_loaded) { await _model.LoadDetailAsync(); RenderRx(); } }
    private async void Filter_Changed(object sender, SelectionChangedEventArgs args) { if (_loaded) await ApplyFilterAsync(); }
    private Task ApplyFilterAsync() => _model.FilterAsync((FilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() is { Length: > 0 } filter ? filter : null, RepeatBox.Text.Trim());
    private async void Apply_Click(object sender, RoutedEventArgs args) => await ApplyFilterAsync();
    private async void Previous_Click(object sender, RoutedEventArgs args) => await _model.PreviousAsync();
    private async void Next_Click(object sender, RoutedEventArgs args) => await _model.NextAsync();
    private async void NextRx_Click(object sender, RoutedEventArgs args) { await _model.LoadDetailAsync(true); RenderRx(); }
    private async void PreviousRx_Click(object sender, RoutedEventArgs args) { await _model.LoadDetailAsync(previousRx: true); RenderRx(); }
    private async void Folder_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var selectedDirectory = _model.SelectedRunDirectory;
            var path = await Task.Run(() =>
            {
                if (selectedDirectory is not null && Directory.Exists(selectedDirectory)) return selectedDirectory;
                // Deleted or empty results must still leave the storage folder accessible.
                Directory.CreateDirectory(_resultsRootDirectory);
                return _resultsRootDirectory;
            });
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(path))
                _model.ReportError("결과 폴더를 열지 못했습니다: " + path);
        }
        catch (Exception ex) { _model.ReportError("결과 폴더 열기 실패: " + ex.Message); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(_model.RxBytes)) RenderRx(); }
    internal void UpdateLogPresentation(SequenceLogDisplayOptions options)
    {
        if (_displayOptions == options) return;
        _displayOptions = options;
        RenderRx();
    }
    private void RenderRx()
    {
        if (!_loaded) return;
        RxText.Blocks.Clear();
        if (_model.SelectedStep is not { } selected) return;
        foreach (var line in SequenceTimelineFormatter.Format(selected, _model.RxSlices, _model.RxEncoding,
            _model.RxDecoderPrefix, _displayOptions, _model.RxPageContinuation))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
            paragraph.Inlines.Add(new Run { Text = SequenceTimelineFormatter.FormatPrefix(line, _displayOptions),
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
            foreach (var part in line.Parts)
            {
                var run = new Run { Text = part.Text };
                if (part.Matched) { run.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed); }
                else if (line.Direction.StartsWith("TX")) { run.Foreground = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue); }
                paragraph.Inlines.Add(run);
            }
            RxText.Blocks.Add(paragraph);
        }
    }
}
