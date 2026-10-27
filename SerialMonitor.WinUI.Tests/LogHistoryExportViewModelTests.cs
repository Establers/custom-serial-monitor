using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class LogHistoryExportViewModelTests
{
    [Fact]
    public async Task WorkerProgress_IsCoalescedWithoutUpdatingUiPropertiesUntilTheUiPolls()
    {
        var model = new LogHistoryExportViewModel();
        model.Begin(CancellationToken.None);
        var propertyChanges = 0;
        model.PropertyChanged += (_, _) => propertyChanges++;

        await Task.Run(() =>
        {
            for (var i = 0; i <= 50_000; i++) model.Report(new(i, 100_000));
        });

        Assert.Equal(0, propertyChanges);
        Assert.Equal(0, model.Percent);
        Assert.True(model.IsIndeterminate);
        model.RefreshProgress();
        Assert.Equal(50, model.Percent);
        Assert.False(model.IsIndeterminate);
        Assert.Contains("50,000", model.DetailText);
        var changesAfterPoll = propertyChanges;
        model.RefreshProgress();
        Assert.Equal(changesAfterPoll, propertyChanges);
        model.Finish();
        Assert.Equal(Visibility.Collapsed, model.Visibility);
    }

    [Fact]
    public void Cancel_CancelsOnlyThisExport_AndTheNextExportStartsFresh()
    {
        using var lifetime = new CancellationTokenSource();
        var model = new LogHistoryExportViewModel();
        var first = model.Begin(lifetime.Token);
        model.Report(new(40, 100));
        model.RefreshProgress();

        model.CancelCommand.Execute(null);

        Assert.True(first.IsCancellationRequested);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.False(model.CancelCommand.CanExecute(null));
        Assert.Equal(Visibility.Visible, model.Visibility);
        Assert.True(model.IsIndeterminate);
        model.Finish();
        var second = model.Begin(lifetime.Token);
        Assert.False(second.IsCancellationRequested);
        Assert.Equal(0, model.Percent);
        Assert.True(model.IsIndeterminate);
        Assert.True(model.CancelCommand.CanExecute(null));
        model.Finish();
        Assert.False(model.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Shutdown_CancelsTheCurrentExport_WithoutRequiringUiProgressCallbacks()
    {
        using var lifetime = new CancellationTokenSource();
        var model = new LogHistoryExportViewModel();
        var token = model.Begin(lifetime.Token);

        lifetime.Cancel();

        Assert.True(token.IsCancellationRequested);
        model.Finish();
        Assert.Equal(Visibility.Collapsed, model.Visibility);
        Assert.False(model.CancelCommand.CanExecute(null));
    }
}
