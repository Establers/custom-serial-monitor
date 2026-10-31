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
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _loaded;
    public SequenceResultsWindow(SequenceRunStore store, ElementTheme theme)
    {
        InitializeComponent();
        Root.RequestedTheme = theme;
        _model = new(store); Root.DataContext = _model;
        _model.PropertyChanged += ModelChanged;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Tick;
        Root.Loaded += async (_, _) => { _loaded = true; await _model.RefreshRunsAsync(); if (_loaded) _timer.Start(); };
        Closed += (_, _) => { _loaded = false; _timer.Stop(); _timer.Tick -= Tick; _model.PropertyChanged -= ModelChanged; _model.Dispose(); };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(640, 800));
    }
    private async void Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_model.SelectedRun is null) await _model.RefreshRunsAsync();
        else await _model.RefreshAsync();
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
        if (_model.SelectedRunDirectory is { } path)
            try { await Windows.System.Launcher.LaunchFolderPathAsync(path); }
            catch (Exception ex) { _model.ReportError("결과 폴더 열기 실패: " + ex.Message); }
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(_model.RxBytes)) RenderRx(); }
    private void RenderRx()
    {
        if (!_loaded) return;
        RxText.Blocks.Clear();
        if (_model.SelectedStep is not { } selected) return;
        foreach (var line in SequenceTimelineFormatter.Format(selected, _model.RxSlices, _model.RxEncoding, _model.RxDecoderPrefix))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
            paragraph.Inlines.Add(new Run { Text = line.Time + "  ", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
            paragraph.Inlines.Add(new Run { Text = line.Direction.PadRight(4) + " ",
                Foreground = new SolidColorBrush(line.Direction.StartsWith("TX") ? Microsoft.UI.Colors.DodgerBlue : Microsoft.UI.Colors.MediumSeaGreen) });
            foreach (var part in line.Parts)
            {
                var run = new Run { Text = part.Text };
                if (part.Matched) { run.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed); }
                paragraph.Inlines.Add(run);
            }
            RxText.Blocks.Add(paragraph);
        }
    }
}
