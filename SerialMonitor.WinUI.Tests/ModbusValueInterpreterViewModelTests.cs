using System.Globalization;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ModbusValueInterpreterViewModelTests
{
    [Fact]
    public void SelectionAndSettingsReinterpretOnlyTheSelectedResponseAndKeepOriginalBytes()
    {
        var model = new ModbusValueInterpreterViewModel();
        Assert.Equal(Visibility.Collapsed, model.Visibility);
        var frame = Response("0000C841");
        var original = frame.Bytes.ToArray();
        model.SetFrame(frame);
        Assert.Equal(Visibility.Visible, model.EditorVisibility);
        model.FormatIndex = 4;
        model.SwapBytes = model.SwapWords = true;
        Assert.Equal("+0–+1: 25", model.ValuesText);
        model.ScaleText = "0.1";
        model.UnitText = "°C";
        Assert.Contains("2.5 °C", model.ValuesText);
        Assert.Contains("raw 25", model.ValuesText);
        Assert.Equal(original, frame.Bytes.ToArray());
        model.SetFrame(null);
        Assert.Empty(model.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.Visibility);
        model.SetFrame(frame);
        Assert.Equal(4, model.FormatIndex);
        Assert.Contains("2.5 °C", model.ValuesText);
    }

    [Fact]
    public void InvalidInputsClearOldValuesAndRecoverWithoutTruncatingInput()
    {
        var model = new ModbusValueInterpreterViewModel();
        model.SetFrame(Response("00FA0064"));
        foreach (var text in new[] { "", "NaN", "Infinity", "1e9999", "1,000.5" })
        {
            model.ScaleText = text;
            Assert.Empty(model.ValuesText);
            Assert.NotEmpty(model.ValidationError);
        }
        model.ScaleText = "0.1";
        Assert.Empty(model.ValidationError);
        Assert.Contains("25", model.ValuesText);
        model.OffsetText = "2";
        Assert.Empty(model.ValuesText);
        Assert.NotEmpty(model.ValidationError);
        model.OffsetText = "1";
        Assert.StartsWith("+1: ", model.ValuesText);
        model.UnitText = new string('x', 33);
        Assert.Equal(33, model.UnitText.Length);
        Assert.Empty(model.ValuesText);
        Assert.NotEmpty(model.ValidationError);
        model.UnitText = "rpm";
        Assert.Empty(model.ValidationError);
        Assert.Contains("rpm", model.ValuesText);
    }

    [Fact]
    public void OddRegisterCountsAreExplicitAndSixteenBitTypesDisableWordSwap()
    {
        var model = new ModbusValueInterpreterViewModel { FormatIndex = 4 };
        model.SetFrame(Response("123441C80000ABCD"));
        model.OffsetText = "1";
        Assert.Contains("+1–+2: 25", model.ValuesText);
        Assert.Contains("+3", model.RemainderText);
        model.FormatIndex = 0;
        Assert.False(model.IsWordSwapEnabled);
        Assert.Empty(model.RemainderText);
        model.FormatIndex = 4;
        model.OffsetText = "3";
        Assert.Empty(model.ValuesText);
        Assert.NotEmpty(model.RemainderText);
    }

    [Fact]
    public void BadCrcAndRequestsNeverReusePreviousInterpretation()
    {
        var model = new ModbusValueInterpreterViewModel();
        var frame = Response("00FA");
        model.SetFrame(frame);
        Assert.NotEmpty(model.ValuesText);
        foreach (var unsupported in new[] { frame with { CrcStatus = ModbusCrcStatus.Invalid }, frame with { Kind = ModbusFrameKind.Request } })
        {
            model.SetFrame(unsupported);
            Assert.Empty(model.ValuesText);
            Assert.Equal(Visibility.Collapsed, model.EditorVisibility);
            Assert.Contains("CRC-valid", model.Help);
        }
    }

    [Theory]
    [InlineData("en-US", "0.1")]
    [InlineData("ko-KR", "0.1")]
    [InlineData("de-DE", "0,1")]
    [InlineData("de-DE", "0.1")]
    public void MultiplierAcceptsLocalDecimalOrInvariantDotWithoutThousands(string culture, string text)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var model = new ModbusValueInterpreterViewModel { ScaleText = text };
            model.SetFrame(Response("00FA"));
            Assert.Empty(model.ValidationError);
            Assert.Contains("25", model.ValuesText);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task ProtocolTabRestoresPreferencesAndDropsInterpretationOnClearOrProtocolChange()
    {
        await using var analyzer = new ProtocolAnalyzer();
        var model = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        var response = Response("41C80000");
        analyzer.TryEnqueue(response.Bytes, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot().RetainedCount < 1) await Task.Delay(5, timeout.Token);
        model.Refresh();
        model.SelectedFrame = Assert.Single(model.Frames);
        model.ValueInterpreter.FormatIndex = 4;
        Assert.Contains(": 25", model.ValueInterpreter.ValuesText);
        model.IsVisible = false;
        Assert.Empty(model.ValueInterpreter.ValuesText);
        model.IsVisible = true;
        Assert.Contains(": 25", model.ValueInterpreter.ValuesText);
        model.Clear();
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.Equal(4, model.ValueInterpreter.FormatIndex);
        model.SelectedFrame = new ProtocolFrameRow(response);
        model.SelectedProtocolIndex = 2;
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.Visibility);
    }

    [Fact]
    public void EditorBindingsAndTranslationsRemainPresentAndDoNotTruncateNumericInputs()
    {
        var root = RepoRoot();
        var markup = XDocument.Load(Path.Combine(root, "SerialMonitor.WinUI", "Views", "ModbusValueInterpreterView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.All(markup.Descendants(presentation + "TextBox"), box => Assert.Null(box.Attribute("MaxLength")));
        Assert.Contains(markup.Descendants(presentation + "ComboBox"), box => ((string?)box.Attribute("SelectedIndex"))?.Contains("FormatIndex, Mode=TwoWay") == true);
        foreach (var locale in new[] { "en-US", "ko-KR" })
        {
            var resources = XDocument.Load(Path.Combine(root, "SerialMonitor.WinUI", "Strings", locale, "Resources.resw"));
            var entries = resources.Descendants("data").Where(entry => ((string?)entry.Attribute("name"))?.StartsWith("ModbusValues", StringComparison.Ordinal) == true).ToArray();
            Assert.Equal(17, entries.Length);
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace((string?)entry.Element("value"))));
        }
    }

    private static ModbusRtuFrame Response(string data)
    {
        var payload = Convert.FromHexString(data);
        var body = new byte[] { 1, 3, (byte)payload.Length }.Concat(payload).ToArray();
        var crc = ModbusRtuCrc.Compute(body);
        return new(1, 1, DateTimeOffset.UnixEpoch, ProtocolDirection.Rx,
            body.Concat(new byte[] { (byte)crc, (byte)(crc >> 8) }).ToArray(), ModbusFrameKind.Response, ModbusCrcStatus.Valid);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "SerialMonitor.WinUI", "Views"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
