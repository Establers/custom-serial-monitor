using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;
using SerialMonitor.WinUI.Views;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private LogTimePickerViewModel? _navigationViewModel;
    private bool _navigationDialogOpen;
    private long _navigationActivityVersion;

    private void InitializeLogNavigationUi()
    {
        _navigationViewModel = new LogTimePickerViewModel(new XtermLogNavigationBridge(
            async (script, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isXtermReady || IsClosingOrClosed || _isVisualAppendSuspendedForMinimize) return "null";
                var result = await ExecuteXtermScriptAsync(script).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }));
        UpdateLogNavigationActivity();
    }

    private void UpdateLogNavigationActivity()
    {
        if (_navigationViewModel is null) return;
        var active = _isXtermReady && !IsClosingOrClosed && !_isWindowMinimized && _navigationDialogOpen;
        if (!active) _navigationActivityVersion++;
        _navigationViewModel.SetActive(active);
    }

    private async Task OpenLogNavigationAsync()
    {
        if (_navigationDialogOpen || _navigationViewModel is null || !_isXtermReady ||
            IsClosingOrClosed || _isVisualAppendSuspendedForMinimize) return;

        _navigationDialogOpen = true;
        UpdateLogNavigationActivity();
        try
        {
            var model = _navigationViewModel;
            var openingVersion = _navigationActivityVersion;
            await model.PrepareForOpenAsync();
            // A minimize/restore cycle can finish while metadata is in flight.
            // Do not show a canceled opening session with empty selectors.
            if (openingVersion != _navigationActivityVersion || !_isXtermReady ||
                IsClosingOrClosed || _isWindowMinimized || _isVisualAppendSuspendedForMinimize) return;
            var inputWidth = Math.Clamp(Root.ActualWidth - 80, 240, 280);
            var content = new LogNavigationView
            {
                ViewModel = model,
                Width = inputWidth,
                MaxWidth = 280
            };
            var dialog = new LogNavigationDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
                Title = UiText.Get("NativeLogTimePickerTitle", "Go to time"),
                Content = content,
                PrimaryButtonText = UiText.Get("NativeLogNavigationGo", "Go"),
                SecondaryButtonText = UiText.Get("NativeLogNavigationBack", "Back"),
                CloseButtonText = UiText.Get("NativeLogNavigationCancel", "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.SetBinding(ContentDialog.IsPrimaryButtonEnabledProperty, new Binding
            {
                Source = model, Path = new PropertyPath(nameof(LogTimePickerViewModel.CanNavigate))
            });
            dialog.SetBinding(ContentDialog.IsSecondaryButtonEnabledProperty, new Binding
            {
                Source = model, Path = new PropertyPath(nameof(LogTimePickerViewModel.CanGoBack))
            });
            dialog.Opened += (_, _) => content.FocusInput();
            dialog.PrimaryButtonClick += async (_, clickArgs) =>
            {
                var deferral = clickArgs.GetDeferral();
                try { clickArgs.Cancel = !await model.NavigateAsync(); }
                catch (Exception ex)
                {
                    clickArgs.Cancel = true;
                    _viewModel.RecordXtermLayoutError($"Log navigation action failed: {ex.Message}");
                }
                finally { deferral.Complete(); }
            };
            dialog.SecondaryButtonClick += async (_, clickArgs) =>
            {
                var deferral = clickArgs.GetDeferral();
                try { clickArgs.Cancel = !await model.BackAsync(); }
                catch (Exception ex)
                {
                    clickArgs.Cancel = true;
                    _viewModel.RecordXtermLayoutError($"Log navigation Back failed: {ex.Message}");
                }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _viewModel.RecordXtermLayoutError($"Log navigation could not be opened: {ex.Message}");
        }
        finally
        {
            _navigationDialogOpen = false;
            UpdateLogNavigationActivity();
        }
    }

    private void DisposeLogNavigationUi()
    {
        _navigationActivityVersion++;
        _navigationViewModel?.Dispose();
    }
}
