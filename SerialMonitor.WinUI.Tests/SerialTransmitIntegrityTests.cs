using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialTransmitIntegrityTests
{
    [Theory]
    [InlineData(TxLineEndingMode.None)]
    [InlineData(TxLineEndingMode.Cr)]
    [InlineData(TxLineEndingMode.Lf)]
    [InlineData(TxLineEndingMode.Crlf)]
    public void EveryHexByteAndCrcArePreservedWithoutAnyTerminator(TxLineEndingMode ending)
    {
        var bytes = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
        foreach (var input in new[]
        {
            Convert.ToHexString(bytes), Convert.ToHexString(bytes).ToLowerInvariant(),
            string.Join(" ", bytes.Select(value => value.ToString("X2"))),
            string.Join(",\r\n\t", bytes.Select(value => "0x" + value.ToString("x2")))
        })
            Assert.Equal(bytes, TxPayloadEncoder.Encode(input, TxSendMode.Hex, ending));
        // Modbus RTU request: unit 1, read holding register 0, quantity 1, CRC 0A84 (low byte first).
        Assert.Equal(new byte[] { 1, 3, 0, 0, 0, 1, 0x84, 0x0A },
            TxPayloadEncoder.Encode("01 03 00 00 00 01 84 0A", TxSendMode.Hex, ending));
    }

    [Theory]
    [InlineData(TxLineEndingMode.None, "")]
    [InlineData(TxLineEndingMode.Cr, "\r")]
    [InlineData(TxLineEndingMode.Lf, "\n")]
    [InlineData(TxLineEndingMode.Crlf, "\r\n")]
    public void TerminalKeepsLiteralContentAndAppendsOnlyTheSelectedEnding(TxLineEndingMode ending, string suffix)
    {
        const string command = " \t\0\u0001\u001b\"\\가🙂\r\n ";
        Assert.Equal(Encoding.UTF8.GetBytes(command + suffix), TxPayloadEncoder.Encode(command, TxSendMode.Terminal, ending));
    }

    [Fact]
    public async Task InvalidUtf16CannotSilentlyTransmitReplacementBytes()
    {
        await using var serial = new SerialService();
        await serial.ConnectAsync(new() { PortName = "MOCK" }, new(), default);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => serial.SendAsync(new("test", "set \ud800 value"), default));
        Assert.Equal(0, serial.WrittenByteCount);
    }

    [Theory]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.None, "text", "none")]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.Crlf, "text", "crlf")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.Crlf, "hex", "none")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.Lf, "hex", "none")]
    public async Task SavedCommandMetadataDescribesTheSameTxAsTheGui(TxSendMode mode, TxLineEndingMode ending, string expectedMode, string expectedEnding)
    {
        // Exercise the actual GUI list_commands adapter without creating a window,
        // starting automation discovery or touching the user's profile.
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_startupProfileTask", Task.CompletedTask);
        Set(model, "_currentUiSettings", new UiSettings { TxSendMode = mode });
        Set(model, "_currentSerialSettings", new SerialSettings { TxLineEnding = ending });
        Set(model, "_selectedTxLineEnding", ending);
        var commands = new CommandViewModel();
        commands.SavedCommands.Add(new("Packet", "AA 55"));
        Set(model, "<Commands>k__BackingField", commands);
        var method = typeof(MainViewModel).GetMethod("InvokeAutomationOnUiAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<object>)method.Invoke(model, ["list_commands", JsonSerializer.SerializeToElement(new { }), CancellationToken.None])!;
        var command = Assert.Single(Assert.IsType<AutomationCommandPage>(result).Commands);
        Assert.Equal(expectedMode, command.TxMode);
        Assert.Equal(expectedEnding, command.LineEnding);
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
}
