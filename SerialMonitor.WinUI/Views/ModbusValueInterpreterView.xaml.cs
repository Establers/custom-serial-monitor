using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SerialMonitor.WinUI.Views;

public sealed partial class ModbusValueInterpreterView : UserControl
{
    public ModbusValueInterpreterView() => InitializeComponent();

    private void View_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Keep the three fields on one line when space permits, and retain
        // usable input widths in the narrow split-pane Inspector.
        var narrow = args.NewSize.Width < 285;
        Grid.SetColumn(ScaleFields, narrow ? 0 : 1);
        Grid.SetRow(ScaleFields, narrow ? 1 : 0);
        FieldsGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        FieldsGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        FieldsGrid.RowSpacing = narrow ? 3 : 0;
    }
}
