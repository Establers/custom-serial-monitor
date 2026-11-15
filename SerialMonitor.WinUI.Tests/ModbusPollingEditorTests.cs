using System.Xml.Linq;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusPollingEditorTests
{
    [Fact]
    public void EditorDraftAndReturnedConfigurationAreIndependentOfTheSourceProfile()
    {
        var original = Workspace();
        original.Groups[0].Settings.Items = [new() { Address = 10, Name = "Mode", ValueNames = [new() { Value = 1, Name = "Automatic" }] }];
        var editor = new ModbusPollingEditorViewModel(original);
        editor.NameText = "Changed group";
        var items = editor.CreateItemEditorSettings()!;
        items.Items[0].Name = "Changed item";
        items.Items[0].ValueNames[0].Name = "Auto";
        editor.ApplyItemDefinitions(items.Items);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal("First", original.Groups[0].Name);
        Assert.Equal("Mode", original.Groups[0].Settings.Items[0].Name);
        Assert.Equal("Automatic", original.Groups[0].Settings.Items[0].ValueNames[0].Name);
        Assert.Equal("Changed group", result.Groups[0].Name);
        Assert.Equal("Changed item", result.Groups[0].Settings.Items[0].Name);
        result.Groups[0].Settings.Items[0].Name = "Mutated result";
        Assert.True(editor.TryApply(out var next), editor.Error);
        Assert.Equal("Changed item", next.Groups[0].Settings.Items[0].Name);
    }

    [Fact]
    public void DuplicateGetsANewIdentityAndIndependentSettingsWhileKeepingTheWireTarget()
    {
        var original = Workspace();
        original.Groups[0].Settings.Items = [new() { Address = 10, Name = "Value" }];
        var editor = new ModbusPollingEditorViewModel(original);
        Assert.True(editor.DuplicateGroup(), editor.Error);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(3, result.Groups.Count);
        var duplicate = result.Groups[2];
        Assert.NotEqual(result.Groups[0].Id, duplicate.Id);
        Assert.Equal(duplicate.Id, result.SelectedGroupId);
        Assert.Equal(result.Groups[0].Settings.UnitId, duplicate.Settings.UnitId);
        Assert.Equal(result.Groups[0].Settings.Address, duplicate.Settings.Address);
        duplicate.Settings.Items[0].Name = "Other";
        Assert.Equal("Value", result.Groups[0].Settings.Items[0].Name);
        Assert.Equal(2, original.Groups.Count);
    }

    [Fact]
    public void DisabledStateAndEveryReadSettingRoundTripThroughTheEditor()
    {
        var editor = new ModbusPollingEditorViewModel(Workspace())
        {
            NameText = "Drive", Enabled = false, UnitText = "0x0A", FunctionIndex = 3,
            AddressText = "0x20", QuantityText = "4", IntervalText = "1200", TimeoutText = "2300",
            FormatIndex = (int)ModbusValueType.Float32, ScaleText = "0.1", SwapBytes = true, SwapWords = true
        };
        Assert.True(editor.TryApply(out var result), editor.Error);
        var group = result.Groups[0];
        Assert.False(group.Enabled);
        Assert.Equal("Drive", group.Name);
        Assert.Equal(10, group.Settings.UnitId);
        Assert.Equal(4, group.Settings.Function);
        Assert.Equal(32, group.Settings.Address);
        Assert.Equal(4, group.Settings.Quantity);
        Assert.Equal(1200, group.Settings.IntervalMs);
        Assert.Equal(2300, group.Settings.TimeoutMs);
        Assert.Equal(ModbusValueType.Float32, group.Settings.ValueType);
        Assert.Equal(0.1, group.Settings.Scale);
        Assert.True(group.Settings.SwapBytes);
        Assert.True(group.Settings.SwapWords);
    }

    [Theory]
    [InlineData("0", "10", "4")]
    [InlineData("248", "10", "4")]
    [InlineData("1", "0x10000", "1")]
    [InlineData("1", "65535", "2")]
    [InlineData("1", "10", "126")]
    public void InvalidWireDraftNeverReplacesTheStoredGroup(string unit, string address, string quantity)
    {
        var original = Workspace();
        var editor = new ModbusPollingEditorViewModel(original) { UnitText = unit, AddressText = address, QuantityText = quantity };
        Assert.False(editor.TryApply(out _));
        Assert.NotEmpty(editor.Error);
        Assert.Equal(address, editor.AddressText);
        Assert.Equal(10, editor.Groups[0].Group!.Settings.Address);
        Assert.Equal(10, original.Groups[0].Settings.Address);
    }

    [Fact]
    public void ValidDraftIsIncludedWhenSelectingAnotherGroup()
    {
        var original = Workspace();
        var editor = new ModbusPollingEditorViewModel(original);
        var second = editor.Groups[1];
        editor.NameText = "Edited";
        editor.UnitText = "3";
        editor.ApplyItemDefinitions([new() { Address = 10, Name = "Mode" }]);
        editor.SelectedGroup = second;
        Assert.Same(second, editor.SelectedGroup);
        Assert.Equal("Second", editor.NameText);
        Assert.Equal("2", editor.UnitText);
        Assert.Empty(editor.Error);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal("second", result.SelectedGroupId);
        Assert.Equal("first", result.Groups[0].Id);
        Assert.Equal("Edited", result.Groups[0].Name);
        Assert.Equal(3, result.Groups[0].Settings.UnitId);
        Assert.Equal("Mode", Assert.Single(result.Groups[0].Settings.Items).Name);
        Assert.Equal("First", original.Groups[0].Name);
        Assert.Empty(original.Groups[0].Settings.Items);
        result.Groups[0].Settings.Items[0].Name = "Changed result";
        Assert.True(editor.TryApply(out var next), editor.Error);
        Assert.Equal("Mode", next.Groups[0].Settings.Items[0].Name);
    }

    [Fact]
    public void NewGroupIncludesValidEditsWithoutAddingUntouchedDefaults()
    {
        var original = Workspace();
        var editor = new ModbusPollingEditorViewModel(original) { NameText = "Edited existing" };
        editor.NewGroup();
        Assert.Null(editor.SelectedGroup);
        Assert.Equal("Edited existing", editor.Groups[0].Group!.Name);
        Assert.Equal("first", editor.Groups[0].Group!.Id);
        Assert.Equal(2, editor.Groups.Count);
        editor.NameText = "New area";
        editor.AddressText = "100";
        editor.NewGroup();
        Assert.Null(editor.SelectedGroup);
        var added = editor.Groups[2].Group!;
        Assert.Equal("New area", added.Name);
        Assert.Equal(100, added.Settings.Address);
        Assert.NotEqual("first", added.Id);
        editor.NewGroup();
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal(added.Id, result.SelectedGroupId);
        Assert.Equal(2, original.Groups.Count);
        Assert.Equal("First", original.Groups[0].Name);
    }

    [Theory]
    [InlineData(false, "new")]
    [InlineData(false, "other")]
    [InlineData(false, "deselect")]
    [InlineData(true, "new")]
    [InlineData(true, "other")]
    public void InvalidNavigationKeepsTheDraftUntilItCanBeIncluded(bool newDraft, string action)
    {
        var original = Workspace();
        var editor = new ModbusPollingEditorViewModel(original);
        if (newDraft) editor.NewGroup();
        var selected = editor.SelectedGroup;
        var second = editor.Groups[1];
        var entries = editor.Groups.ToArray();
        editor.NameText = "Keep this draft";
        editor.UnitText = "248";
        editor.AddressText = "10";
        editor.QuantityText = "4";
        editor.ApplyItemDefinitions([new() { Address = 10, Name = "Keep this item" }]);
        Navigate();
        Assert.Same(selected, editor.SelectedGroup);
        Assert.Equal("Keep this draft", editor.NameText);
        Assert.Equal("248", editor.UnitText);
        Assert.Equal(entries, editor.Groups);
        Assert.NotEmpty(editor.Error);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, editor.ErrorVisibility);
        Assert.False(editor.TryApply(out _));

        editor.UnitText = "3";
        Assert.Equal("Keep this item", Assert.Single(editor.CreateItemEditorSettings()!.Items).Name);
        Navigate();
        Assert.Empty(editor.Error);
        if (action == "other") Assert.Same(second, editor.SelectedGroup);
        else Assert.Null(editor.SelectedGroup);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(newDraft ? 3 : 2, result.Groups.Count);
        var saved = result.Groups[newDraft ? 2 : 0];
        Assert.Equal("Keep this draft", saved.Name);
        Assert.Equal(3, saved.Settings.UnitId);
        Assert.Equal("Keep this item", Assert.Single(saved.Settings.Items).Name);
        Assert.Equal("First", original.Groups[0].Name);
        Assert.Empty(original.Groups[0].Settings.Items);

        void Navigate()
        {
            if (action == "new") editor.NewGroup();
            else editor.SelectedGroup = action == "deselect" ? null : second;
        }
    }

    [Fact]
    public void CollectionAndSelectionReentryCannotRedirectOrDuplicateAutomaticInclusion()
    {
        var editor = new ModbusPollingEditorViewModel(Workspace());
        var second = editor.Groups[1];
        var collectionReentries = 0;
        var selectionReentries = 0;
        editor.Groups.CollectionChanged += (_, _) =>
        {
            collectionReentries++;
            editor.SelectedGroup = null;
            editor.NewGroup();
        };
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(editor.SelectedGroup) || selectionReentries++ >= 10) return;
            editor.SelectedGroup = null;
            editor.NewGroup();
        };
        editor.NameText = "Included first";
        editor.SelectedGroup = second;
        Assert.Same(second, editor.SelectedGroup);
        Assert.Equal("Second", editor.NameText);
        Assert.Equal("Included first", editor.Groups[0].Group!.Name);
        editor.NameText = "Included second";
        editor.NewGroup();
        Assert.Null(editor.SelectedGroup);
        editor.NameText = "Included third";
        editor.SelectedGroup = editor.Groups[0];
        Assert.Equal("Included first", editor.NameText);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(new[] { "Included first", "Included second", "Included third" }, result.Groups.Select(group => group.Name));
        Assert.Equal("first", result.SelectedGroupId);
        Assert.True(collectionReentries > 0);
        Assert.True(selectionReentries > 0);
    }

    [Fact]
    public void DetachedSelectionCannotCommitOrReplaceTheCurrentDraft()
    {
        var editor = new ModbusPollingEditorViewModel(Workspace());
        var detached = editor.SelectedGroup;
        editor.NameText = "Included";
        editor.SelectedGroup = editor.Groups[1];
        var current = editor.SelectedGroup;
        editor.NameText = "Still editing";
        editor.SelectedGroup = detached;
        Assert.Same(current, editor.SelectedGroup);
        Assert.Equal("Still editing", editor.NameText);
        Assert.Equal("Second", current!.Group!.Name);
        Assert.Empty(editor.Error);
    }

    [Fact]
    public void ImportedInvalidGroupsCanBeRepairedOneAtATimeBeforeApplying()
    {
        var original = Workspace();
        original.Groups.ForEach(group => group.Settings = null!);
        var editor = new ModbusPollingEditorViewModel(original);
        editor.SelectedGroup = editor.Groups[1];
        Assert.Equal("Second", editor.NameText);
        Repair(200);
        editor.SelectedGroup = editor.Groups[0];
        Assert.Equal(200, editor.Groups[1].Group!.Settings.Address);
        Assert.False(editor.TryApply(out _));
        Repair(100);
        editor.NewGroup();
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(new[] { 100, 200 }, result.Groups.Select(group => group.Settings.Address));
        Assert.All(original.Groups, group => Assert.Null(group.Settings));

        void Repair(int address)
        {
            editor.UnitText = "1";
            editor.AddressText = address.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.QuantityText = "2";
            editor.FunctionIndex = 2;
        }
    }

    [Fact]
    public void EmptyAndClearedWorkspacesRemainExplicitlyEmptyUntilAGroupIsAdded()
    {
        var editor = new ModbusPollingEditorViewModel(new());
        Assert.True(editor.TryApply(out var empty), editor.Error);
        Assert.Empty(empty.Groups);
        Assert.Null(empty.SelectedGroupId);
        editor.NewGroup();
        Assert.True(editor.TryApply(out empty), editor.Error);
        Assert.Empty(empty.Groups);
        Assert.True(editor.SaveGroup(), editor.Error);
        Assert.True(editor.TryApply(out var added), editor.Error);
        Assert.Single(added.Groups);
        editor.ClearGroups();
        Assert.True(editor.TryApply(out empty), editor.Error);
        Assert.Empty(empty.Groups);
        Assert.Null(empty.SelectedGroupId);
    }

    [Fact]
    public void MissingGroupsAndSettingsStayBlockedAndNeverBecomeDefaultWireTargetsOnRename()
    {
        var original = Workspace();
        original.Groups = null!;
        var editor = new ModbusPollingEditorViewModel(original);
        Assert.False(editor.TryApply(out _));
        editor.ClearGroups();
        Assert.True(editor.TryApply(out var empty), editor.Error);
        Assert.Empty(empty.Groups);
        Assert.Null(original.Groups);

        original = Workspace();
        original.Groups[0].Settings = null!;
        editor = new(original);
        Assert.Equal("", editor.UnitText);
        Assert.Equal("", editor.AddressText);
        Assert.Equal(-1, editor.FunctionIndex);
        editor.NameText = "Renamed invalid group";
        Assert.False(editor.TryApply(out _));
        Assert.Null(editor.Groups[0].Group!.Settings);
        editor.UnitText = "2";
        editor.AddressText = "100";
        editor.QuantityText = "3";
        editor.FunctionIndex = 2;
        Assert.True(editor.SaveGroup(), editor.Error);
        Assert.True(editor.TryApply(out var corrected), editor.Error);
        Assert.Equal(2, corrected.Groups[0].Settings.UnitId);
        Assert.Equal(100, corrected.Groups[0].Settings.Address);
        Assert.Null(original.Groups[0].Settings);
    }

    [Fact]
    public void NullGroupsCanBeExplicitlyRemovedAndImportedFlagsNeedAnExplicitUpdate()
    {
        var original = Workspace();
        original.Groups.Insert(0, null!);
        var editor = new ModbusPollingEditorViewModel(original);
        Assert.False(editor.TryApply(out _));
        editor.SelectedGroup = editor.Groups[0];
        editor.RemoveGroup();
        Assert.True(editor.TryApply(out var corrected), editor.Error);
        Assert.Equal(2, corrected.Groups.Count);

        original = Workspace();
        original.HasInvalidMetadata = true;
        original.Groups[0].HasInvalidMetadata = true;
        original.Groups[0].Enabled = false;
        editor = new(original);
        Assert.False(editor.TryApply(out _));
        Assert.False(editor.Enabled);
        Assert.True(editor.SaveGroup(), editor.Error);
        Assert.True(editor.TryApply(out corrected), editor.Error);
        Assert.False(corrected.Groups[0].Enabled);
        Assert.False(corrected.HasInvalidMetadata);
        Assert.False(corrected.Groups[0].HasInvalidMetadata);
        Assert.True(original.HasInvalidMetadata);
        Assert.True(original.Groups[0].HasInvalidMetadata);
    }

    [Fact]
    public void GroupAndCombinedReadCapsRejectAddAndDuplicateWithoutTruncation()
    {
        var full = new ModbusPollingSettings { Groups = Enumerable.Range(0, 32).Select(index => Group($"g{index}", $"Group {index}", index + 1)).ToList(), SelectedGroupId = "g0" };
        var editor = new ModbusPollingEditorViewModel(full);
        Assert.False(editor.DuplicateGroup());
        editor.NewGroup();
        editor.NameText = new string('n', 129);
        Assert.False(editor.SaveGroup());
        Assert.Equal(129, editor.NameText.Length);
        editor.NameText = "Overflow group";
        Assert.False(editor.SaveGroup());
        Assert.Equal(32, editor.Groups.Count);

        full = new() { Groups = Enumerable.Range(0, 4).Select(index => new ModbusPollingGroup { Id = $"c{index}", Settings = new() { Function = 1, Quantity = 2000 } }).ToList(), SelectedGroupId = "c0" };
        editor = new(full);
        editor.NewGroup();
        editor.FunctionIndex = 0;
        editor.QuantityText = "193";
        Assert.False(editor.SaveGroup());
        editor.QuantityText = "192";
        Assert.True(editor.SaveGroup(), editor.Error);
        Assert.True(editor.TryApply(out var accepted), editor.Error);
        Assert.Equal(8192, accepted.Groups.Sum(group => group.Settings.Quantity));
    }

    [Fact]
    public void NestedItemDraftCanCorrectInvalidDefinitionsWithoutModifyingTheGroupBeforeApply()
    {
        var original = Workspace();
        original.Groups[0].Settings.Items = [new() { Address = 10, ValueType = (ModbusValueType)(-1) }];
        var editor = new ModbusPollingEditorViewModel(original);
        var itemSettings = editor.CreateItemEditorSettings();
        Assert.NotNull(itemSettings);
        Assert.Equal(-1, (int)itemSettings.Items[0].ValueType);
        Assert.False(editor.TryApply(out _));
        editor.ApplyItemDefinitions([new() { Address = 10, Name = "Fixed", ValueType = ModbusValueType.UInt16 }]);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal("Fixed", result.Groups[0].Settings.Items[0].Name);
        Assert.Equal(-1, (int)original.Groups[0].Settings.Items[0].ValueType);
    }

    [Fact]
    public void OrphanSelectionIsPreservedUntilTheUserSelectsAnExistingGroup()
    {
        var original = Workspace();
        original.SelectedGroupId = "missing";
        var editor = new ModbusPollingEditorViewModel(original);
        Assert.Null(editor.SelectedGroup);
        Assert.True(editor.TryApply(out var unchanged), editor.Error);
        Assert.Equal("missing", unchanged.SelectedGroupId);
        editor.SelectedGroup = editor.Groups[1];
        Assert.True(editor.TryApply(out var selected), editor.Error);
        Assert.Equal("second", selected.SelectedGroupId);
    }

    [Fact]
    public void GroupSelectorRemainsAvailableDuringPollingAndEditorValidationIsVisible()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var monitor = XDocument.Load(Source("Views", "ModbusRtuView.xaml"));
        var selector = Assert.Single(monitor.Descendants(p + "ComboBox").Where(element => (string?)element.Attribute(x + "Name") == "PollingGroupSelector"));
        Assert.Equal("{Binding IsGroupSelectionEnabled}", (string?)selector.Attribute("IsEnabled"));
        Assert.Equal("{Binding SelectedPollingGroup, Mode=TwoWay}", (string?)selector.Attribute("SelectedItem"));
        Assert.Equal("{Binding PollingGroupsVisibility}", (string?)selector.Attribute("Visibility"));
        var markup = XDocument.Load(Source("Views", "ModbusPollingEditorDialog.xaml"));
        var scroll = Assert.Single(markup.Descendants(p + "ScrollViewer").Where(element => (string?)element.Attribute(x + "Name") == "EditorScroll"));
        Assert.DoesNotContain(markup.Descendants(p + "TextBox"), element => element.Attribute("MaxLength") is not null);
        Assert.DoesNotContain(scroll.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding Error}");
        Assert.Contains(markup.Descendants(p + "Button"), element => (string?)element.Attribute("Click") == "Items_Click");
    }

    private static ModbusPollingGroup Group(string id, string name, int unit) => new()
    {
        Id = id, Name = name, Settings = new() { UnitId = unit, Address = 10, Quantity = 4 }
    };
    private static ModbusPollingSettings Workspace() => new()
    {
        Groups = [Group("first", "First", 1), Group("second", "Second", 2)], SelectedGroupId = "first"
    };
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
