using System.Xml.Linq;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusItemEditorTests
{
    [Fact]
    public void EditingAndApplyingKeepTheOriginalProfileAndReturnedCopiesIndependent()
    {
        var original = Settings();
        original.Items = [new() { Address = 10, Name = "Mode", ValueNames = [new() { Value = 1, Name = "Auto" }] }];
        var editor = new ModbusItemEditorViewModel(original, 10);
        editor.NameText = "Operating mode";
        editor.ValueNamesText = "0=Manual\n1=Automatic";
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal("Mode", original.Items[0].Name);
        Assert.Equal("Auto", original.Items[0].ValueNames[0].Name);
        Assert.Equal("Operating mode", result[0].Name);
        result[0].Name = "Other";
        result[0].ValueNames[0].Name = "Changed";
        Assert.True(editor.TryApply(out var second), editor.Error);
        Assert.Equal("Operating mode", second[0].Name);
        Assert.Equal("Manual", second[0].ValueNames[0].Name);
    }

    [Fact]
    public void SparseMixedWidthDefinitionsUseTheirOwnScaleOrderUnitsAndPermissions()
    {
        var editor = new ModbusItemEditorViewModel(Settings());
        editor.NameText = "Temperature";
        editor.FormatIndex = (int)ModbusValueType.Int16;
        editor.ScaleText = "0.1";
        editor.UnitText = "°C";
        editor.AccessIndex = (int)ModbusItemAccess.ReadWrite;
        Assert.True(editor.SaveItem(), editor.Error);
        editor.NewItem();
        Assert.Equal("11", editor.AddressText);
        editor.AddressText = "0x0D";
        editor.NameText = "Pressure";
        editor.FormatIndex = (int)ModbusValueType.Float32;
        editor.ScaleText = "1";
        editor.UnitText = "kPa";
        editor.SwapWords = true;
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Equal(2, items.Count);
        Assert.Equal(10, items[0].Address);
        Assert.Equal(0.1, items[0].Scale);
        Assert.Equal(ModbusItemAccess.ReadWrite, items[0].Access);
        Assert.Equal(13, items[1].Address);
        Assert.Equal(2, items[1].RegisterCount);
        Assert.True(items[1].SwapWords);
        Assert.Equal(ModbusItemAccess.ReadOnly, items[1].Access);
    }

    [Fact]
    public void ApplyRejectsOverlappingDraftWithoutChangingTheSavedDefinition()
    {
        var settings = Settings();
        settings.Items = [new() { Address = 11, Name = "32-bit value", ValueType = ModbusValueType.Float32 }];
        var editor = new ModbusItemEditorViewModel(settings);
        editor.AddressText = "12";
        editor.NameText = "Overlapping";
        Assert.False(editor.TryApply(out _));
        Assert.NotEmpty(editor.Error);
        Assert.Single(editor.Items);
        Assert.Equal("32-bit value", editor.Items[0].Definition!.Name);
        Assert.Equal("32-bit value", settings.Items[0].Name);
    }

    [Theory]
    [InlineData("0x10000", 0, "Valid")]
    [InlineData("65536", 0, "Valid")]
    [InlineData("15", 4, "Valid")]
    public void CompleteInvalidAddressesAndSplitMultiwordItemsCannotBeApplied(string address, int format, string name)
    {
        var editor = new ModbusItemEditorViewModel(Settings()) { AddressText = address, FormatIndex = format, NameText = name };
        Assert.False(editor.TryApply(out _));
        Assert.NotEmpty(editor.Error);
        Assert.Empty(editor.Items);
        Assert.Equal(address, editor.AddressText);
    }

    [Fact]
    public void LongNamesAndDuplicateValueNamesAreRejectedWithoutInputTruncation()
    {
        var editor = new ModbusItemEditorViewModel(Settings()) { NameText = new string('n', 129) };
        Assert.False(editor.SaveItem());
        Assert.Equal(129, editor.NameText.Length);
        editor.NameText = "Mode";
        editor.ValueNamesText = "0=Manual\n1=manual";
        Assert.False(editor.SaveItem());
        editor.ValueNamesText = "0=Manual\n1=Automatic";
        Assert.True(editor.SaveItem(), editor.Error);
        Assert.Equal(2, editor.Items[0].Definition!.ValueNames.Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void InputAreasCannotAcquireWritableDefinitions(byte function)
    {
        var settings = Settings();
        settings.Function = function;
        var editor = new ModbusItemEditorViewModel(settings) { NameText = "Input" };
        Assert.False(editor.CanChooseAccess);
        editor.AccessIndex = (int)ModbusItemAccess.ReadWrite;
        Assert.True(editor.CanChooseAccess);
        Assert.False(editor.SaveItem());
        editor.AccessIndex = (int)ModbusItemAccess.ReadOnly;
        Assert.False(editor.CanChooseAccess);
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Equal(ModbusItemAccess.ReadOnly, Assert.Single(items).Access);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ImportedWritableInputCanBeCorrectedToReadOnlyWithoutDeletingTheDefinition(byte function)
    {
        var settings = Settings();
        settings.Function = function;
        settings.Items = [new() { Address = 10, Name = "Input", Access = ModbusItemAccess.ReadWrite }];
        var editor = new ModbusItemEditorViewModel(settings, 10);
        Assert.True(editor.CanChooseAccess);
        Assert.False(editor.TryApply(out _));
        var notifications = new List<string?>();
        editor.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        editor.AccessIndex = (int)ModbusItemAccess.ReadOnly;
        Assert.Contains(nameof(editor.CanChooseAccess), notifications);
        Assert.False(editor.CanChooseAccess);
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Equal(ModbusItemAccess.ReadOnly, Assert.Single(items).Access);
        Assert.Equal(ModbusItemAccess.ReadWrite, settings.Items[0].Access);
    }

    [Fact]
    public void CoilDefinitionsStartWithBooleanCompatibleFormatAndMayHaveValueNames()
    {
        var settings = Settings();
        settings.Function = 1;
        settings.ValueType = ModbusValueType.Float32;
        settings.Scale = 0.1;
        settings.SwapBytes = true;
        settings.SwapWords = true;
        var editor = new ModbusItemEditorViewModel(settings) { NameText = "Output", ValueNamesText = "0=Off\n1=On" };
        Assert.False(editor.IsRegister);
        Assert.Equal(0, editor.FormatIndex);
        Assert.Equal("1", editor.ScaleText);
        Assert.False(editor.SwapBytes);
        Assert.False(editor.SwapWords);
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Equal(2, Assert.Single(items).ValueNames.Count);
    }

    [Fact]
    public void InvalidImportedEntriesStayBlockedUntilExplicitRemovalOrClear()
    {
        var settings = Settings();
        settings.Items = [null!, new() { Address = 12, ValueType = (ModbusValueType)(-1) }];
        var editor = new ModbusItemEditorViewModel(settings);
        Assert.False(editor.TryApply(out _));
        editor.SelectedItem = editor.Items[0];
        Assert.NotNull(editor.SelectedItem);
        editor.RemoveItem();
        Assert.Single(editor.Items);
        Assert.False(editor.TryApply(out _));
        editor.ClearItems();
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Empty(items);
        Assert.Equal(2, settings.Items.Count);

        settings.Items = null!;
        editor = new(settings);
        Assert.False(editor.TryApply(out _));
        editor.ClearItems();
        Assert.True(editor.TryApply(out items), editor.Error);
        Assert.Empty(items);
    }

    [Fact]
    public void UnsavedDraftCannotDisappearWhenAnotherItemIsSelected()
    {
        var settings = Settings();
        settings.Items = [new() { Address = 10, Name = "First" }, new() { Address = 11, Name = "Second" }];
        var editor = new ModbusItemEditorViewModel(settings, 10);
        var selected = editor.SelectedItem;
        editor.NameText = "Edited";
        editor.SelectedItem = editor.Items[1];
        Assert.Same(selected, editor.SelectedItem);
        Assert.Equal("Edited", editor.NameText);
        Assert.NotEmpty(editor.Error);
        Assert.True(editor.SaveItem(), editor.Error);
        editor.SelectedItem = editor.Items[1];
        Assert.Equal("Second", editor.NameText);
    }

    [Fact]
    public void SelectingEitherWordOfAnExistingItemEditsThatDefinition()
    {
        var settings = Settings();
        settings.Items = [new() { Address = 10, Name = "Pressure", ValueType = ModbusValueType.Float32 }];
        var editor = new ModbusItemEditorViewModel(settings, 11);
        Assert.Equal("10", editor.AddressText);
        Assert.Equal("Pressure", editor.NameText);
        Assert.Single(editor.Items);
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Single(items);
    }

    [Fact]
    public void EditorSupportsFullInputAndKeepsValidationOutsideTheScrollableForm()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var markup = XDocument.Load(Source("Views", "ModbusItemEditorDialog.xaml"));
        var scroll = Assert.Single(markup.Descendants(p + "ScrollViewer").Where(element => (string?)element.Attribute(x + "Name") == "EditorScroll"));
        Assert.DoesNotContain(markup.Descendants(p + "TextBox"), element => element.Attribute("MaxLength") is not null);
        Assert.DoesNotContain(scroll.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding Error}");
        Assert.Contains(markup.Descendants(p + "TextBox"), element => (string?)element.Attribute("Text") == "{Binding ValueNamesText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" && (string?)element.Attribute("AcceptsReturn") == "True");
        var monitor = XDocument.Load(Source("Views", "ModbusRtuView.xaml"));
        Assert.Contains(monitor.Descendants(p + "ComboBox"), element => (string?)element.Attribute("SelectedIndex") == "{Binding WriteModeIndex, Mode=TwoWay}");
        Assert.Contains(monitor.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding NameText}");
        Assert.Contains(monitor.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding WritePreview}");
    }

    [Fact]
    public void UnsignedHexadecimalValueNameCannotWrapIntoASignedCommand()
    {
        var editor = new ModbusItemEditorViewModel(Settings());
        editor.FormatIndex = (int)ModbusValueType.Int16;
        editor.ValueNamesText = "0xFFFFFFFFFFFFFFFF=Unexpected negative";
        Assert.False(editor.SaveItem());
        Assert.Empty(editor.Items);
        editor.ValueNamesText = "-1=Explicit negative";
        Assert.True(editor.SaveItem(), editor.Error);
        Assert.True(editor.TryApply(out var items), editor.Error);
        Assert.Equal(-1, Assert.Single(Assert.Single(items).ValueNames).Value);
    }

    private static ModbusMonitorSettings Settings() => new() { Address = 10, Quantity = 6 };
    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, "SerialMonitor.WinUI", .. parts]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The source file was not found.");
    }
}
