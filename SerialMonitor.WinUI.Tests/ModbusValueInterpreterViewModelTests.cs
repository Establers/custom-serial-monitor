using System.Globalization;
using System.Text.RegularExpressions;
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
        Assert.Contains(": 2.5", model.ValuesText);
        Assert.Contains("raw 25", model.ValuesText);
        Assert.Equal(original, frame.Bytes.ToArray());
        model.SetFrame(null);
        Assert.Empty(model.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.Visibility);
        model.SetFrame(frame);
        Assert.Equal(4, model.FormatIndex);
        Assert.Contains(": 2.5", model.ValuesText);
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
        model.OffsetText = new string('9', 40);
        Assert.Equal(40, model.OffsetText.Length);
        Assert.Empty(model.ValuesText);
        Assert.NotEmpty(model.ValidationError);
        model.OffsetText = "0";
        Assert.Empty(model.ValidationError);
        Assert.Contains(": 25", model.ValuesText);
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
        foreach (var (unsupported, reason) in new[]
        {
            (frame with { CrcStatus = ModbusCrcStatus.Invalid }, "CRC error"),
            (frame with { Kind = ModbusFrameKind.Request }, "Request packet")
        })
        {
            model.SetFrame(unsupported);
            Assert.Empty(model.ValuesText);
            Assert.Equal(Visibility.Collapsed, model.EditorVisibility);
            Assert.Contains(reason, model.Help);
        }
    }

    [Theory]
    [InlineData(ProtocolDirection.Tx)]
    [InlineData(ProtocolDirection.Rx)]
    public async Task CrcValidRequestAndFollowingResponseExplainTheirRolesWhenSelected(ProtocolDirection requestDirection)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        analyzer.TryEnqueue(Convert.FromHexString("010300000002C40B"), requestDirection, 1, DateTimeOffset.Now);
        analyzer.TryEnqueue(Response("00FA0064").Bytes, ProtocolDirection.Rx, 1, DateTimeOffset.Now);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot().RetainedCount < 2) await Task.Delay(5, timeout.Token);
        model.Refresh();
        model.SelectedFrame = model.Frames.Single(row => ((ModbusRtuFrame)row.Frame).Kind == ModbusFrameKind.Request);
        Assert.Contains("CRC: Valid", model.Details);
        Assert.Contains("Request packet", model.ValueInterpreter.Help);
        Assert.DoesNotContain("CRC", model.ValueInterpreter.Help);
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.EditorVisibility);
        model.SelectedFrame = model.Frames.Single(row => ((ModbusRtuFrame)row.Frame).Kind == ModbusFrameKind.Response);
        Assert.Equal(Visibility.Visible, model.ValueInterpreter.EditorVisibility);
        Assert.Contains("+0: 250", model.ValueInterpreter.ValuesText);
        Assert.Contains("+1: 100", model.ValueInterpreter.ValuesText);
        Assert.DoesNotContain("Request packet", model.ValueInterpreter.Help);
    }

    [Fact]
    public void UnavailableHintsDistinguishExceptionsIncompleteAmbiguousAndUnsupportedDataFromCrcErrors()
    {
        var model = new ModbusValueInterpreterViewModel();
        var frames = new[]
        {
            (Frame(Convert.FromHexString("018302"), ModbusFrameKind.Exception), "Exception response"),
            (Frame(Convert.FromHexString("010600000001"), ModbusFrameKind.RequestOrResponse), "ambiguous"),
            (Response("00FA") with { CrcStatus = ModbusCrcStatus.Unavailable, Kind = ModbusFrameKind.Incomplete }, "Incomplete frame"),
            (Frame(Convert.FromHexString("01010101"), ModbusFrameKind.Response), "supports FC03"),
            (Frame(Convert.FromHexString("01030400FA"), ModbusFrameKind.Response), "invalid address, length or data layout")
        };
        foreach (var (frame, reason) in frames)
        {
            model.SetFrame(Response("00FA"));
            Assert.NotEmpty(model.ValuesText);
            model.SetFrame(frame);
            Assert.Contains(reason, model.Help);
            Assert.DoesNotContain("CRC", model.Help);
            Assert.Empty(model.ValuesText);
            Assert.Equal(Visibility.Collapsed, model.EditorVisibility);
        }
        var corrupted = Response("00FA").Bytes.ToArray();
        corrupted[^1] ^= 1;
        model.SetFrame(Response("00FA") with { Bytes = corrupted });
        Assert.Contains("CRC error", model.Help);
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
        var source = File.ReadAllText(Path.Combine(root, "SerialMonitor.WinUI", "ViewModels", "ModbusValueInterpreterViewModel.cs"));
        var referencedKeys = Regex.Matches(source, "\"(ModbusValues[^\"]+)\"").Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        foreach (var locale in new[] { "en-US", "ko-KR" })
        {
            var resources = XDocument.Load(Path.Combine(root, "SerialMonitor.WinUI", "Strings", locale, "Resources.resw"));
            var entries = resources.Descendants("data").Where(entry => ((string?)entry.Attribute("name"))?.StartsWith("ModbusValues", StringComparison.Ordinal) == true).ToArray();
            Assert.Equal(referencedKeys, entries.Select(entry => (string)entry.Attribute("name")!).Order());
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace((string?)entry.Element("value"))));
        }
    }

    private static ModbusRtuFrame Response(string data)
    {
        var payload = Convert.FromHexString(data);
        var body = new byte[] { 1, 3, (byte)payload.Length }.Concat(payload).ToArray();
        return Frame(body, ModbusFrameKind.Response);
    }

    private static ModbusRtuFrame Frame(byte[] body, ModbusFrameKind kind)
    {
        var crc = ModbusRtuCrc.Compute(body);
        return new(1, 1, DateTimeOffset.UnixEpoch, ProtocolDirection.Rx,
            body.Concat(new byte[] { (byte)crc, (byte)(crc >> 8) }).ToArray(), kind, ModbusCrcStatus.Valid);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "SerialMonitor.WinUI", "Views"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
