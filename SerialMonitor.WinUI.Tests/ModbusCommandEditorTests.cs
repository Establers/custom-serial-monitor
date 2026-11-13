using System.Xml.Linq;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Modbus;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusCommandEditorTests
{
    [Fact]
    public void DraftChangesAndReturnedSettingsNeverMutateTheOriginalCommand()
    {
        var original = Settings();
        var editor = new ModbusCommandEditorViewModel(original);
        editor.CommandNameText = "Changed";
        editor.ValueText = "25.3";
        editor.FormatIndex = (int)ModbusValueType.Int16;
        editor.ScaleText = "0.1";
        editor.EngineeringUnitText = "°C";
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal("First", original.Commands[0].Name);
        Assert.Equal("6", original.Commands[0].Steps[0].Value);
        Assert.Equal("Changed", result.Commands[0].Name);
        Assert.Equal(new ushort[] { 253 }, result.Commands[0].Steps[0].EncodeValues());
        result.Commands[0].Steps[0].Value = "Other";
        Assert.True(editor.TryApply(out var next), editor.Error);
        Assert.Equal("25.3", next.Commands[0].Steps[0].Value);
    }

    [Fact]
    public void EmptyEditorDoesNotCreateOrRunAnImplicitDefaultCommand()
    {
        var editor = new ModbusCommandEditorViewModel(new());
        Assert.Empty(editor.CreateSettings().Commands);
        editor.NewCommand();
        editor.NewStep();
        Assert.Empty(editor.CreateSettings().Commands);
        editor.ValueText = "1";
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Single(Assert.Single(result.Commands).Steps);
        editor.ClearCommands();
        Assert.Empty(editor.CreateSettings().Commands);
        Assert.Null(editor.CreateSettings().SelectedCommandId);
    }

    [Fact]
    public void DuplicateCommandGetsANewIdentityAndOwnsDeepStepCopies()
    {
        var original = Settings();
        original.Commands[0].Steps[0].Item.ValueNames = [new() { Value = 6, Name = "Enable" }];
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.True(editor.DuplicateCommand(), editor.Error);
        var result = editor.CreateSettings();
        Assert.Equal(3, result.Commands.Count);
        Assert.NotEqual(result.Commands[0].Id, result.Commands[2].Id);
        Assert.Equal(result.Commands[2].Id, result.SelectedCommandId);
        result.Commands[2].Steps[0].Item.ValueNames[0].Name = "Different";
        Assert.Equal("Enable", result.Commands[0].Steps[0].Item.ValueNames[0].Name);
        Assert.Equal(2, original.Commands.Count);
    }

    [Fact]
    public void StepAddCloneReorderAndDeletePreserveTheExplicitExecutionOrder()
    {
        var editor = new ModbusCommandEditorViewModel(Settings());
        Assert.True(editor.DuplicateStep(), editor.Error);
        editor.ValueText = "7";
        Assert.True(editor.SaveStep(), editor.Error);
        editor.NewStep();
        editor.UnitText = "3";
        editor.AddressText = "100";
        editor.ValueText = "15";
        editor.VerificationIndex = (int)ModbusCommandVerificationMode.AcknowledgementOnly;
        Assert.True(editor.SaveStep(), editor.Error);
        Assert.True(editor.MoveStep(-1));
        Assert.Equal(new[] { "6", "15", "7" }, editor.Steps.Select(entry => entry.Definition!.Value));
        editor.RemoveStep();
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(new[] { "6", "7" }, result.Commands[0].Steps.Select(step => step.Value));
        Assert.All(result.Commands[0].Steps, step => Assert.Equal(20, step.Item.Address));
    }

    [Fact]
    public void DirtyStepAndCommandCannotDisappearWhenSelectionChanges()
    {
        var original = Settings();
        original.Commands[0].Steps.Add(Step("Next", "7"));
        var editor = new ModbusCommandEditorViewModel(original);
        var firstStep = editor.SelectedStep;
        editor.ValueText = "15";
        editor.SelectedStep = editor.Steps[1];
        Assert.Same(firstStep, editor.SelectedStep);
        Assert.Equal("15", editor.ValueText);
        Assert.NotEmpty(editor.Error);
        Assert.True(editor.SaveStep(), editor.Error);
        var firstCommand = editor.SelectedCommand;
        editor.SelectedCommand = editor.Items[1];
        Assert.Same(firstCommand, editor.SelectedCommand);
        Assert.True(editor.SaveCommand(), editor.Error);
        editor.SelectedCommand = editor.Items[1];
        Assert.Equal("Second", editor.CommandNameText);
    }

    [Fact]
    public void AcknowledgementOnlyIsDistinctFromReadbackAndDoesNotNeedAMonitoredArea()
    {
        var editor = new ModbusCommandEditorViewModel(Settings())
        {
            UnitText = "9", AddressText = "0xF000", ValueText = "1",
            VerificationIndex = (int)ModbusCommandVerificationMode.AcknowledgementOnly
        };
        Assert.Equal(Visibility.Collapsed, editor.StatusVisibility);
        Assert.NotEmpty(editor.VerificationHelp);
        Assert.True(editor.TryApply(out var result), editor.Error);
        var step = result.Commands[0].Steps[0];
        Assert.Equal(9, step.UnitId);
        Assert.Equal(0xF000, step.Item.Address);
        Assert.Equal(ModbusCommandVerificationMode.AcknowledgementOnly, step.Verification.Mode);
        Assert.Equal(6, step.BuildPacket()[1]);
        Assert.Contains("HEX:", editor.Preview);
    }

    [Fact]
    public void StatusMaskAndExpectedValueRemainRawAndIndependentOfTheWrittenItemFormat()
    {
        var editor = new ModbusCommandEditorViewModel(Settings())
        {
            FormatIndex = (int)ModbusValueType.Float32, ScaleText = "0.1", ValueText = "1.5", SwapWords = true,
            VerificationIndex = (int)ModbusCommandVerificationMode.Status, StatusFunctionIndex = 3,
            StatusAddressText = "0x40", MaskText = "0x000C", ExpectedText = "0x0008",
            VerificationTimeoutText = "7000", VerificationIntervalText = "200"
        };
        Assert.Equal(Visibility.Visible, editor.StatusVisibility);
        Assert.True(editor.TryApply(out var result), editor.Error);
        var step = result.Commands[0].Steps[0];
        Assert.Equal(new ushort[] { 0, 0x4170 }, step.EncodeValues());
        Assert.Equal(16, step.BuildPacket()[1]);
        Assert.Equal(4, step.Verification.Function);
        Assert.Equal(64, step.Verification.Address);
        Assert.Equal(12, step.Verification.Mask);
        Assert.Equal(8, step.Verification.ExpectedValue);
        Assert.Equal(7000, step.Verification.TimeoutMs);
        Assert.Equal(200, step.Verification.IntervalMs);
    }

    [Theory]
    [InlineData("0", "20", "6")]
    [InlineData("248", "20", "6")]
    [InlineData("1", "0x10000", "6")]
    [InlineData("1", "20", "65536")]
    [InlineData("1", "20", "1.5")]
    public void InvalidWriteDraftNeverReplacesTheSavedStep(string unit, string address, string value)
    {
        var editor = new ModbusCommandEditorViewModel(Settings()) { UnitText = unit, AddressText = address, ValueText = value };
        Assert.False(editor.TryApply(out _));
        Assert.NotEmpty(editor.Error);
        Assert.Equal(address, editor.AddressText);
        Assert.Equal("6", editor.Items[0].Definition!.Steps[0].Value);
    }

    [Theory]
    [InlineData("0", "0", "3000", "100")]
    [InlineData("1", "2", "3000", "100")]
    [InlineData("0x10000", "0", "3000", "100")]
    [InlineData("1", "1", "100", "200")]
    public void InvalidStatusConditionsCannotBeApplied(string mask, string expected, string timeout, string interval)
    {
        var editor = new ModbusCommandEditorViewModel(Settings())
        {
            VerificationIndex = 1, MaskText = mask, ExpectedText = expected,
            VerificationTimeoutText = timeout, VerificationIntervalText = interval
        };
        Assert.False(editor.TryApply(out _));
        Assert.NotEmpty(editor.Error);
    }

    [Fact]
    public void CoilFieldsCanCorrectAnImportedInvalidFormatBeforeLockingToBooleanEncoding()
    {
        var original = Settings();
        var step = original.Commands[0].Steps[0];
        step.Function = 1;
        step.Item.ValueType = ModbusValueType.Float32;
        step.Item.Scale = 0.1;
        step.Item.SwapBytes = true;
        step.Item.SwapWords = true;
        step.Value = "1";
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.True(editor.CanEditFormat);
        Assert.True(editor.CanEditScale);
        Assert.True(editor.CanSwapBytes);
        Assert.True(editor.CanSwapWords);
        Assert.False(editor.TryApply(out _));
        editor.FormatIndex = 0;
        editor.ScaleText = "1";
        editor.SwapBytes = false;
        editor.SwapWords = false;
        Assert.False(editor.CanEditFormat);
        Assert.False(editor.CanEditScale);
        Assert.False(editor.CanSwapBytes);
        Assert.False(editor.CanSwapWords);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.Equal(5, result.Commands[0].Steps[0].BuildPacket()[1]);
    }

    [Fact]
    public void MissingImportedStepItemAndVerificationNeverAcquireDefaultTargetsOnRename()
    {
        var original = Settings();
        original.Commands[0].Steps[0].Item = null!;
        original.Commands[0].Steps[0].Verification = null!;
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.Equal("", editor.AddressText);
        Assert.Equal(-1, editor.FormatIndex);
        Assert.Equal(-1, editor.VerificationIndex);
        editor.StepNameText = "Renamed";
        Assert.False(editor.TryApply(out _));
        Assert.Null(editor.Items[0].Definition!.Steps[0].Item);
        editor.AreaIndex = 1;
        editor.AddressText = "100";
        editor.FormatIndex = 0;
        editor.ScaleText = "1";
        editor.ValueNamesText = "";
        editor.VerificationIndex = 2;
        Assert.True(editor.TryApply(out var corrected), editor.Error);
        Assert.Equal(100, corrected.Commands[0].Steps[0].Item.Address);
        Assert.Null(original.Commands[0].Steps[0].Item);

        original = Settings();
        original.Commands[0].Steps[0] = null!;
        editor = new(original);
        Assert.Equal("", editor.UnitText);
        editor.StepNameText = "Renamed";
        Assert.False(editor.TryApply(out _));
        editor.RemoveStep();
        editor.NewStep();
        editor.ValueText = "1";
        Assert.True(editor.TryApply(out corrected), editor.Error);
        Assert.Single(corrected.Commands[0].Steps);
    }

    [Fact]
    public void MalformedCollectionsAndFlagsRemainBlockedUntilExplicitCorrectionOrRemoval()
    {
        var original = Settings();
        original.Commands = null!;
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.False(editor.TryApply(out _));
        editor.ClearCommands();
        Assert.Empty(editor.CreateSettings().Commands);
        Assert.Null(original.Commands);

        original = Settings();
        original.HasInvalidMetadata = true;
        original.Commands[0].HasInvalidMetadata = true;
        original.Commands[0].Steps[0].HasInvalidMetadata = true;
        original.Commands[0].Steps[0].Verification.HasInvalidMetadata = true;
        editor = new(original);
        Assert.False(editor.TryApply(out _));
        Assert.False(editor.SaveCommand());
        Assert.False(editor.SaveStep());
        editor.AreaIndex = 1;
        editor.FormatIndex = 0;
        editor.ValueText = "6";
        editor.VerificationIndex = 0;
        Assert.True(editor.SaveStep(), editor.Error);
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.False(result.HasInvalidMetadata);
        Assert.False(result.Commands[0].HasInvalidMetadata);
        Assert.False(result.Commands[0].Steps[0].HasInvalidMetadata);
        Assert.False(result.Commands[0].Steps[0].Verification.HasInvalidMetadata);
        Assert.True(original.Commands[0].Steps[0].HasInvalidMetadata);
    }

    [Fact]
    public void MalformedStatusExpectedValueRequiresExplicitConditionEntryBeforeSaving()
    {
        var original = Settings();
        original.Commands[0].Steps[0].Verification = new()
        {
            Mode = ModbusCommandVerificationMode.Status, Function = 3, Address = 100,
            Mask = 1, ExpectedValue = 0, HasInvalidMetadata = true
        };
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.Equal("", editor.ExpectedText);
        Assert.Equal(-1, editor.VerificationIndex);
        editor.StepNameText = "Renamed";
        Assert.False(editor.SaveStep());
        Assert.False(editor.TryApply(out _));
        editor.VerificationIndex = 1;
        editor.StatusFunctionIndex = 2;
        editor.StatusAddressText = "100";
        editor.MaskText = "1";
        editor.ExpectedText = "0";
        editor.VerificationTimeoutText = "3000";
        editor.VerificationIntervalText = "100";
        Assert.True(editor.TryApply(out var result), editor.Error);
        Assert.False(result.Commands[0].Steps[0].Verification.HasInvalidMetadata);
        Assert.Equal(0, result.Commands[0].Steps[0].Verification.ExpectedValue);
        Assert.True(original.Commands[0].Steps[0].Verification.HasInvalidMetadata);
    }

    [Fact]
    public void ImportedReadOnlyAccessAndMalformedWriteMetadataCannotBecomeWritableOnRename()
    {
        var original = Settings();
        original.Commands[0].Steps[0].Item.Access = ModbusItemAccess.ReadOnly;
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.Equal(-1, editor.AreaIndex);
        editor.StepNameText = "Renamed";
        Assert.False(editor.SaveStep());
        editor.AreaIndex = 1;
        Assert.True(editor.SaveStep(), editor.Error);

        original = Settings();
        original.Commands[0].Steps[0].HasInvalidMetadata = true;
        editor = new(original);
        editor.StepNameText = "Renamed";
        Assert.Equal(-1, editor.AreaIndex);
        Assert.Equal(-1, editor.FormatIndex);
        Assert.Equal("", editor.ValueText);
        Assert.False(editor.SaveStep());
    }

    [Fact]
    public void NamedValuesAndFullInputRemainValidatedWithoutTruncatingTheWireValue()
    {
        var editor = new ModbusCommandEditorViewModel(Settings()) { ValueNamesText = "6=Enable\n7=Ready", ValueText = "Enable" };
        Assert.True(editor.SaveStep(), editor.Error);
        Assert.Equal(new ushort[] { 6 }, editor.Steps[0].Definition!.EncodeValues());
        editor.ValueText = new string('1', 257);
        Assert.False(editor.SaveStep());
        Assert.Equal(257, editor.ValueText.Length);
        editor.ValueText = "6";
        editor.CommandNameText = new string('n', 129);
        Assert.False(editor.TryApply(out _));
        Assert.Equal(129, editor.CommandNameText.Length);
    }

    [Fact]
    public void CommandAndStepCapsRejectDuplicationWithoutDiscardingEntries()
    {
        var original = Settings();
        original.Commands = Enumerable.Range(0, 32).Select(index => new ModbusCommandDefinition { Id = $"c{index}", Steps = [Step("Value", "1")] }).ToList();
        original.SelectedCommandId = "c0";
        var editor = new ModbusCommandEditorViewModel(original);
        Assert.False(editor.DuplicateCommand());
        Assert.Equal(32, editor.Items.Count);
        original = Settings();
        original.Commands[0].Steps = Enumerable.Range(0, 32).Select(index => Step($"Step {index}", "1")).ToList();
        editor = new(original);
        Assert.False(editor.DuplicateStep());
        Assert.Equal(32, editor.Steps.Count);
    }

    [Fact]
    public void EditorHasNoRunActionAndKeepsErrorsVisibleOutsideTheScrollingForm()
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var markup = XDocument.Load(Source("Views", "ModbusCommandEditorDialog.xaml"));
        var scroll = Assert.Single(markup.Descendants(p + "ScrollViewer").Where(element => (string?)element.Attribute(x + "Name") == "StepEditorScroll"));
        Assert.DoesNotContain(markup.Descendants(p + "TextBox"), element => element.Attribute("MaxLength") is not null);
        Assert.DoesNotContain(scroll.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding Error}");
        Assert.DoesNotContain(markup.Descendants(p + "Button"), element => ((string?)element.Attribute("Click"))?.Contains("Run", StringComparison.Ordinal) == true);
        Assert.Contains(markup.Descendants(p + "TextBlock"), element => (string?)element.Attribute("Text") == "{Binding VerificationHelp}");
        Assert.Contains(markup.Descendants(p + "StackPanel"), element => (string?)element.Attribute("Visibility") == "{Binding StatusVisibility}");
    }

    private static ModbusCommandStep Step(string name, string value) => new()
    {
        Name = name, UnitId = 2, Function = 3, Item = new() { Address = 20, Access = ModbusItemAccess.ReadWrite }, Value = value
    };
    private static ModbusCommandSettings Settings() => new()
    {
        Commands = [new() { Id = "first", Name = "First", Steps = [Step("Start", "6")] }, new() { Id = "second", Name = "Second", Steps = [Step("Stop", "0")] }],
        SelectedCommandId = "first"
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
