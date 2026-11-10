using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusPacketBuilderLayoutTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(1, "UnitText,AddressText,QuantityText")]
    [InlineData(2, "UnitText,AddressText,QuantityText")]
    [InlineData(3, "UnitText,AddressText,QuantityText")]
    [InlineData(4, "UnitText,AddressText,QuantityText")]
    [InlineData(5, "UnitText,AddressText")]
    [InlineData(6, "UnitText,AddressText,ValuesText")]
    [InlineData(7, "UnitText")]
    [InlineData(8, "UnitText,DiagnosticDataText")]
    [InlineData(11, "UnitText")]
    [InlineData(12, "UnitText")]
    [InlineData(15, "UnitText,AddressText,ValuesText")]
    [InlineData(16, "UnitText,AddressText,ValuesText")]
    [InlineData(17, "UnitText")]
    [InlineData(20, "UnitText,FileReadText")]
    [InlineData(21, "UnitText,FileWriteText")]
    [InlineData(22, "UnitText,AddressText,AndMaskText,OrMaskText")]
    [InlineData(23, "UnitText,AddressText,QuantityText,WriteAddressText,ValuesText")]
    [InlineData(24, "UnitText,AddressText")]
    [InlineData(43, "UnitText,ObjectIdText")]
    [InlineData(0, "UnitText,CustomFunctionText,CustomDataText")]
    public void EveryCommandShowsExactlyOneEditorForEachRelevantField(byte function, string fields)
    {
        var model = new ModbusPacketBuilderViewModel();
        model.SelectFunction(function);
        Assert.Equal(fields.Split(',').Order(), VisibleFields(Markup(), model).Order());
        Assert.True(model.CanUsePacket, model.ValidationError);
    }

    [Fact]
    public void ScalarFieldsShareRowsAndReadWriteRangesHaveSeparateRows()
    {
        var markup = Markup();
        foreach (var name in new[] { "ReadRow", "SingleCoilRow", "SingleRegisterRow", "WriteRow", "ReadWriteRow", "DeviceIdRow", "CustomRow", "DiagnosticRow" })
        {
            var row = Named(markup, name);
            Assert.Equal(2, ((string)row.Attribute("ColumnDefinitions")!).Split(',').Length);
            Assert.Contains(row.Elements(), input => (string?)input.Attribute("Grid.Column") == "1");
        }
        Assert.Equal("*,*,*", (string?)Named(markup, "MaskRow").Attribute("ColumnDefinitions"));
        Assert.NotSame(Named(markup, "ReadRow"), Named(markup, "ReadWriteRow"));
        Assert.All(markup.Descendants(Presentation + "TextBox").Where(input => Binding(input) == nameof(ModbusPacketBuilderViewModel.WriteQuantityText)),
            input => Assert.Equal("True", (string?)input.Attribute("IsReadOnly")));
    }

    [Fact]
    public void ScrollbarGuttersProtectEditorsAndThePreviewIsOutsideTheEditorViewport()
    {
        var markup = Markup();
        var editor = Named(markup, "EditorPanel");
        var margins = ((string)editor.Attribute("Margin")!).Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(margins[2] >= 20, "Expanded overlay scrollbar must have its own gutter.");
        Assert.DoesNotContain(Named(markup, "PacketFooter"), Named(markup, "EditorScroll").Descendants());
        var listStyle = Assert.Single(markup.Descendants(Presentation + "Style").Where(style => (string?)style.Attribute(Xaml + "Key") == "PacketListInput"));
        var padding = (string)Assert.Single(listStyle.Elements().Where(setter => (string?)setter.Attribute("Property") == "Padding")).Attribute("Value")!;
        Assert.True(double.Parse(padding.Split(',')[2], CultureInfo.InvariantCulture) >= 20);
    }

    [Fact]
    public void DiagnosticScalarAndMultipleDataEditorsAreMutuallyExclusive()
    {
        var model = new ModbusPacketBuilderViewModel();
        model.SelectFunction(8);
        for (var index = 0; index < model.DiagnosticOptions.Count; index++)
        {
            model.DiagnosticIndex = index;
            Assert.Equal(1, VisibleFields(Markup(), model).Count(field => field == nameof(model.DiagnosticDataText)));
            Assert.Equal(index == 0 ? 2 : 1, model.DiagnosticSelectorColumnSpan);
            Assert.Equal(index == 0 ? Visibility.Collapsed : Visibility.Visible, model.DiagnosticSingleDataVisibility);
        }
    }

    [Fact]
    public void DerivedWriteQuantityTracksTheValidatedPacketAndClearsOnBadInput()
    {
        var model = new ModbusPacketBuilderViewModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
        model.SelectFunction(16);
        model.ValuesText = "10 20 30";
        Assert.Equal("3", model.WriteQuantityText);
        model.SelectFunction(23);
        Assert.Equal("3", model.WriteQuantityText);
        model.WriteAddressText = "65535";
        Assert.False(model.CanUsePacket);
        Assert.Equal("—", model.WriteQuantityText);
        Assert.Contains(nameof(model.WriteQuantityText), notifications);
    }

    [Fact]
    public void EveryFunctionTransitionCanReadWriteQuantityDuringPropertyNotifications()
    {
        byte[] functions = [1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 15, 16, 17, 20, 21, 22, 23, 24, 43, 0];
        foreach (var from in functions)
        foreach (var to in functions)
        {
            var model = new ModbusPacketBuilderViewModel();
            model.SelectFunction(from);
            model.PropertyChanged += (_, change) =>
            {
                var quantity = model.WriteQuantityText;
                if (change.PropertyName == nameof(model.FunctionIndex)) Assert.Equal("—", quantity);
            };
            model.SelectFunction(to);
            Assert.Equal(to is 15 or 16 or 23 ? "1" : "—", model.WriteQuantityText);
        }
        var custom = new ModbusPacketBuilderViewModel { CustomFunctionText = "0x17", CustomDataText = "" };
        custom.SelectFunction(0);
        custom.PropertyChanged += (_, _) => _ = custom.WriteQuantityText;
        custom.SelectFunction(23); // Same on-wire FC as the old short custom PDU.
        Assert.Equal("1", custom.WriteQuantityText);
    }

    private static IEnumerable<string> VisibleFields(XDocument markup, ModbusPacketBuilderViewModel model) =>
        markup.Descendants(Presentation + "TextBox")
            .Where(input => (string?)input.Attribute("IsReadOnly") != "True")
            .Where(input => input.AncestorsAndSelf().All(element => Visible(element, model)))
            .Select(Binding);

    private static bool Visible(XElement element, ModbusPacketBuilderViewModel model)
    {
        var value = (string?)element.Attribute("Visibility");
        if (value is null or "Visible") return true;
        if (!value.StartsWith("{Binding ", StringComparison.Ordinal)) return false;
        var property = value[9..].TrimEnd('}').Split(',')[0];
        return (Visibility)model.GetType().GetProperty(property)!.GetValue(model)! == Visibility.Visible;
    }

    private static string Binding(XElement input) => ((string)input.Attribute("Text")!)[9..].Split([',', '}'])[0];
    private static XElement Named(XDocument markup, string name) => Assert.Single(markup.Descendants().Where(element => (string?)element.Attribute(Xaml + "Name") == name));
    private static XDocument Markup()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null)
        {
            var file = Path.Combine(root.FullName, "SerialMonitor.WinUI", "Views", "ModbusPacketBuilderDialog.xaml");
            if (File.Exists(file)) return XDocument.Load(file);
            root = root.Parent;
        }
        throw new FileNotFoundException("Packet builder XAML was not found.");
    }
}
