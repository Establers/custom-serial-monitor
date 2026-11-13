using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.Core.Modbus;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusItemEditorDialog : ContentDialog
{
    public ModbusItemEditorViewModel Model { get; }
    public IReadOnlyList<ModbusItemDefinition> ResultItems { get; private set; } = [];

    public ModbusItemEditorDialog(ModbusMonitorSettings settings, int? selectedAddress = null)
    {
        Model = new(settings, selectedAddress);
        InitializeComponent();
        DataContext = Model;
    }

    private void New_Click(object sender, RoutedEventArgs args) => Model.NewItem();
    private void SaveItem_Click(object sender, RoutedEventArgs args) => Model.SaveItem();
    private void Remove_Click(object sender, RoutedEventArgs args) => Model.RemoveItem();
    private void Clear_Click(object sender, RoutedEventArgs args) => Model.ClearItems();
    private void Apply_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Model.TryApply(out var items)) { args.Cancel = true; return; }
        ResultItems = items;
    }
}
