using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusPollingEditorDialog : ContentDialog
{
    private bool _restoreQueued, _restoringSelection;
    private int _selectionGeneration;
    public ModbusPollingEditorViewModel Model { get; }
    public ModbusPollingSettings ResultSettings { get; private set; } = new();
    public ModbusMonitorSettings? ItemEditorSettings { get; private set; }

    public ModbusPollingEditorDialog(ModbusPollingSettings settings)
    {
        Model = new(settings);
        InitializeComponent();
        DataContext = Model;
    }

    public void ClearItemEditorRequest() => ItemEditorSettings = null;
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("CommandSpace") is FrameworkElement commandSpace)
            commandSpace.Margin = EditorPanel.Margin;
    }

    private void GroupSelection_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringSelection || _restoreQueued || !IsLoaded) return;
        _restoreQueued = true;
        var generation = _selectionGeneration;
        // A TwoWay setter can reject invalid navigation before WinUI finishes
        // selecting the row. Reconcile only the visible selection afterward.
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _selectionGeneration) return;
            _restoreQueued = false;
            if (!IsLoaded) return;
            _restoringSelection = true;
            try
            {
                if (!ReferenceEquals(GroupList.SelectedItem, Model.SelectedGroup))
                    GroupList.SelectedItem = Model.SelectedGroup;
            }
            finally { _restoringSelection = false; }
        })) _restoreQueued = false;
    }

    private void Dialog_Unloaded(object sender, RoutedEventArgs args)
    {
        _selectionGeneration++;
        _restoreQueued = false;
    }

    private void New_Click(object sender, RoutedEventArgs args) => Model.NewGroup();
    private void Duplicate_Click(object sender, RoutedEventArgs args) => Model.DuplicateGroup();
    private void SaveGroup_Click(object sender, RoutedEventArgs args) => Model.SaveGroup();
    private void Remove_Click(object sender, RoutedEventArgs args) => Model.RemoveGroup();
    private void Clear_Click(object sender, RoutedEventArgs args) => Model.ClearGroups();
    private void Items_Click(object sender, RoutedEventArgs args)
    {
        if (Model.CreateItemEditorSettings() is not { } settings) return;
        ItemEditorSettings = settings;
        // The caller opens the item editor only after this dialog has closed.
        // WinUI permits one ContentDialog per XamlRoot at a time.
        Hide();
    }
    private void Apply_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!Model.TryApply(out var settings)) { args.Cancel = true; return; }
        ResultSettings = settings;
    }
}
