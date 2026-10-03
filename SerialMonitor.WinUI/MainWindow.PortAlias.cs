using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SerialMonitor.WinUI.ViewModels;
using Windows.System;

namespace SerialMonitor.WinUI;

public sealed partial class MainWindow
{
    private readonly PortAliasViewModel _portAlias = new();

    private void InitializePortAliasUi()
    {
        PortAliasButton.DataContext = _portAlias;
        PortAliasEditor.DataContext = _portAlias;
        _portAlias.PropertyChanged += OnPortAliasPropertyChanged;
        UpdatePortAliasSelection();
    }

    private void UpdatePortAliasSelection()
    {
        PortAliasFlyout.Hide();
        _portAlias.SetPort(_viewModel.SelectedActualPortName);
        UpdateWindowTitle();
    }

    private void OnPortAliasPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PortAliasViewModel.WindowTitle) && !IsClosingOrClosed)
            UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        Title = _portAlias.WindowTitle;
#if DEBUG
        if (Environment.GetCommandLineArgs().Any(a => a.StartsWith("--update-preview=", StringComparison.Ordinal)))
            Title += " - Update preview";
#endif
    }

    private void PortAliasFlyout_Opening(object sender, object args) => _portAlias.BeginEdit();

    private void PortAliasFlyout_Closed(object sender, object args) => _portAlias.EndEdit();

    private void PortAliasFlyout_Opened(object sender, object args)
    {
        if (IsClosingOrClosed) return;
        PortAliasTextBox.Focus(FocusState.Programmatic);
        PortAliasTextBox.SelectAll();
    }

    private async void PortAliasApplyButton_Click(object sender, RoutedEventArgs args) => await ApplyPortAliasAsync();

    private async void PortAliasEditor_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            PortAliasFlyout.Hide();
        }
        else if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await ApplyPortAliasAsync();
        }
    }

    private async Task ApplyPortAliasAsync()
    {
        if (await _portAlias.ApplyAsync() && !IsClosingOrClosed) PortAliasFlyout.Hide();
    }
}
