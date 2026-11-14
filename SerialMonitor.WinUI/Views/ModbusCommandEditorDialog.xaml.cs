using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusCommandEditorDialog : ContentDialog
{
    private bool _restoreQueued, _restoringSelection;
    private int _selectionGeneration;
    public ModbusCommandEditorViewModel Model { get; }
    public ModbusCommandSettings ResultSettings { get; private set; } = new();

    public ModbusCommandEditorDialog(ModbusCommandSettings settings)
    {
        Model = new(settings);
        InitializeComponent();
        DataContext = Model;
    }

    private void EditorSelection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringSelection || _restoreQueued || !IsLoaded) return;
        _restoreQueued = true;
        var generation = _selectionGeneration;
        // WinUI may finish applying a user selection after a TwoWay setter has
        // rejected it. Reconcile only the presentation on the next UI turn,
        // after binding and any draft validation have completed.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _selectionGeneration) return;
            _restoreQueued = false;
            if (!IsLoaded) return;
            _restoringSelection = true;
            try
            {
                if (!ReferenceEquals(CommandList.SelectedItem, Model.SelectedCommand))
                    CommandList.SelectedItem = Model.SelectedCommand;
                if (!ReferenceEquals(StepList.SelectedItem, Model.SelectedStep))
                    StepList.SelectedItem = Model.SelectedStep;
            }
            finally { _restoringSelection = false; }
        })) _restoreQueued = false;
    }

    private void Dialog_Unloaded(object sender, RoutedEventArgs args)
    {
        _selectionGeneration++;
        _restoreQueued = false;
    }

    private void NewCommand_Click(object sender, RoutedEventArgs args) => Model.NewCommand();
    private void DuplicateCommand_Click(object sender, RoutedEventArgs args) => Model.DuplicateCommand();
    private void RemoveCommand_Click(object sender, RoutedEventArgs args) => Model.RemoveCommand();
    private void ClearCommands_Click(object sender, RoutedEventArgs args) => Model.ClearCommands();
    private void NewStep_Click(object sender, RoutedEventArgs args) => Model.NewStep();
    private void DuplicateStep_Click(object sender, RoutedEventArgs args) => Model.DuplicateStep();
    private void RemoveStep_Click(object sender, RoutedEventArgs args) => Model.RemoveStep();
    private void MoveUp_Click(object sender, RoutedEventArgs args) => Model.MoveStep(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs args) => Model.MoveStep(1);
    private void Apply_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Model.TryApply(out var settings)) { args.Cancel = true; return; }
        ResultSettings = settings;
    }
}
