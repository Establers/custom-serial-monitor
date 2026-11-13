using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusCommandEditorDialog : ContentDialog
{
    public ModbusCommandEditorViewModel Model { get; }
    public ModbusCommandSettings ResultSettings { get; private set; } = new();

    public ModbusCommandEditorDialog(ModbusCommandSettings settings)
    {
        Model = new(settings);
        InitializeComponent();
        DataContext = Model;
    }

    private void NewCommand_Click(object sender, RoutedEventArgs args) => Model.NewCommand();
    private void DuplicateCommand_Click(object sender, RoutedEventArgs args) => Model.DuplicateCommand();
    private void SaveCommand_Click(object sender, RoutedEventArgs args) => Model.SaveCommand();
    private void RemoveCommand_Click(object sender, RoutedEventArgs args) => Model.RemoveCommand();
    private void ClearCommands_Click(object sender, RoutedEventArgs args) => Model.ClearCommands();
    private void NewStep_Click(object sender, RoutedEventArgs args) => Model.NewStep();
    private void DuplicateStep_Click(object sender, RoutedEventArgs args) => Model.DuplicateStep();
    private void SaveStep_Click(object sender, RoutedEventArgs args) => Model.SaveStep();
    private void RemoveStep_Click(object sender, RoutedEventArgs args) => Model.RemoveStep();
    private void MoveUp_Click(object sender, RoutedEventArgs args) => Model.MoveStep(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs args) => Model.MoveStep(1);
    private void Apply_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Model.TryApply(out var settings)) { args.Cancel = true; return; }
        ResultSettings = settings;
    }
}
