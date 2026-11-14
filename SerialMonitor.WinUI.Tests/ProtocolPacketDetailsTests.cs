using Microsoft.UI.Xaml;
using SerialMonitor.Core.Protocols;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProtocolPacketDetailsTests
{
    [Fact]
    public async Task ReadResponse_ShowsValuesOnceAndKeepsWireDiagnosticsSeparate()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        Assert.True(analyzer.TryEnqueue(Convert.FromHexString("010302007BF867"), ProtocolDirection.Rx, 1, DateTimeOffset.Now));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (analyzer.Snapshot().RetainedCount < 1) await Task.Delay(5, timeout.Token);
        model.Refresh();
        model.SelectedFrame = Assert.Single(model.Frames);

        Assert.Contains("Device 1 · Read holding registers (03)", model.PacketSummary);
        Assert.Contains("Response", model.PacketSummary);
        Assert.Contains("CRC OK", model.PacketSummary);
        Assert.Contains("Registers: 1", model.PacketSummary);
        Assert.DoesNotContain("address", model.PacketSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("received:", model.PacketSummary);
        Assert.DoesNotContain("HEX", model.PacketSummary);
        Assert.Equal("+0: 123", model.ValueInterpreter.ValuesText);
        Assert.Contains("received: 67F8; calculated: 67F8", model.Details);
        Assert.Contains("01 03 02 00 7B F8 67", model.Details);
        Assert.DoesNotContain("+0:", model.Details);
        Assert.DoesNotContain("signed", model.Details);

        var summary = model.PacketSummary;
        var diagnostics = model.Details;
        model.ValueInterpreter.ScaleText = "0.1";
        Assert.Contains("12.3", model.ValueInterpreter.ValuesText);
        Assert.Equal(summary, model.PacketSummary);
        Assert.Equal(diagnostics, model.Details);
        model.IsVisible = false;
        Assert.Empty(model.PacketSummary);
        model.IsVisible = true;
        Assert.Equal(summary, model.PacketSummary);
        Assert.Equal(diagnostics, model.Details);
        model.Clear();
        Assert.Empty(model.PacketSummary);
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.DoesNotContain("67F8", model.Details);
    }

    [Fact]
    public async Task ErrorAndExceptionSelection_KeepTheReasonInTheSummaryAndClearOldValues()
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = new ProtocolViewModel(analyzer) { IsVisible = true, SelectedProtocolIndex = 1 };
        Select(model, Frame("010302007BF867", ModbusFrameKind.Response));
        Assert.Equal("+0: 123", model.ValueInterpreter.ValuesText);

        var invalid = Frame("010302007BF867", ModbusFrameKind.Response) with
        {
            Bytes = Convert.FromHexString("010302007BF866"), CrcStatus = ModbusCrcStatus.Invalid,
            Kind = ModbusFrameKind.Incomplete
        };
        Select(model, invalid);
        Assert.Contains("CRC mismatch", model.PacketSummary);
        Assert.DoesNotContain("Incomplete", model.PacketSummary);
        Assert.DoesNotContain("Registers:", model.PacketSummary);
        Assert.DoesNotContain("received:", model.PacketSummary);
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.Visibility);
        Assert.Contains("received: 66F8; calculated: 67F8", model.Details);

        Select(model, Frame("018302C0F1", ModbusFrameKind.Exception));
        Assert.Contains("Exception response", model.PacketSummary);
        Assert.Contains("Illegal data address", model.PacketSummary);
        Assert.Contains("Exception 02 · CRC OK", model.PacketSummary);
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.Visibility);

        Select(model, Frame("0103", ModbusFrameKind.Incomplete) with { CrcStatus = ModbusCrcStatus.Unavailable });
        Assert.Contains("Incomplete data", model.PacketSummary);
        Assert.Contains("CRC: unavailable", model.Details);
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.Visibility);

        Select(model, Frame("010304007B", ModbusFrameKind.Incomplete) with { CrcStatus = ModbusCrcStatus.Unavailable });
        Assert.DoesNotContain("received:", model.Details);
        Assert.Contains("01 03 04 00 7B", model.Details);

        Select(model, Frame("010302007BF867", ModbusFrameKind.Undecoded));
        Assert.Contains("Undecoded · CRC OK", model.PacketSummary);
        Assert.DoesNotContain("Registers:", model.PacketSummary);
        Assert.Empty(model.ValueInterpreter.ValuesText);
        Assert.Equal(Visibility.Collapsed, model.ValueInterpreter.Visibility);
    }

    [Theory]
    [InlineData(ModbusFrameKind.Request)]
    [InlineData(ModbusFrameKind.RequestOrResponse)]
    public async Task SingleRegisterWrite_UsesInterpreterWithoutClaimingAReadback(ModbusFrameKind kind)
    {
        await using var analyzer = new ModbusRtuAnalyzer();
        var model = new ProtocolViewModel(analyzer) { SelectedProtocolIndex = 1 };
        Select(model, Frame("01061010000FCCCB", kind));
        Assert.Contains("Address (base 0): 4112", model.PacketSummary);
        Assert.DoesNotContain("value:", model.PacketSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("value:", model.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("+0: 15", model.ValueInterpreter.ValuesText);
        Assert.Contains("not a separate readback", model.ValueInterpreter.Help);
        Assert.Equal(kind, ((ModbusRtuFrame)model.SelectedFrame!.Frame).Kind);
    }

    [Fact]
    public void UntrustedBacnetHeader_DoesNotExposeSourceOrDestinationAsKnown()
    {
        var frame = new BacnetMstpFrame(1, 1, DateTimeOffset.Now, ProtocolDirection.Rx,
            Convert.FromHexString("55FF04020100036D4142432F9F"), ProtocolFrameStatus.CrcError,
            ProtocolCrcStatus.Invalid, ProtocolCrcStatus.Unavailable);
        var summary = ProtocolPacketPresentation.Summary(new(frame));
        Assert.Contains("Source — → destination —", summary);
        Assert.Contains("CRC mismatch", summary);
        Assert.DoesNotContain("Source 1", summary);
        Assert.Contains("Header fields are untrusted", ProtocolPacketPresentation.Diagnostics(frame));
    }

    private static ModbusRtuFrame Frame(string hex, ModbusFrameKind kind) =>
        new(1, 1, DateTimeOffset.Now, ProtocolDirection.Rx, Convert.FromHexString(hex), kind, ModbusCrcStatus.Valid);

    private static void Select(ProtocolViewModel model, ModbusRtuFrame frame) => model.SelectedFrame = new(frame);
}
