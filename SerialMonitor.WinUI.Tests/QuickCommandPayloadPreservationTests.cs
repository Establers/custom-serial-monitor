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

public sealed class QuickCommandPayloadPreservationTests
{
    private const string TerminalPayload = "  ECHO value \t ";

    [Theory]
    [InlineData(TxLineEndingMode.None, "")]
    [InlineData(TxLineEndingMode.Cr, "\r")]
    [InlineData(TxLineEndingMode.Lf, "\n")]
    [InlineData(TxLineEndingMode.Crlf, "\r\n")]
    public async Task SavedQuickCommandsKeepExactPayloadThroughSaveAndLoad(TxLineEndingMode ending, string suffix)
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), nameof(QuickCommandPayloadPreservationTests)));
        string directory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            string hex = HexPayload();
            profile.SavedCommands = [new("  Terminal  ", TerminalPayload) { LineEndingMode = ending },
                new("HEX", hex) { LineEndingMode = ending }, new(" ", TerminalPayload)];
            string path = Path.Combine(directory, "profile.json");
            await service.SaveAsync(path, profile, default);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal(TerminalPayload, json.RootElement.GetProperty("SavedCommands")[0].GetProperty("CommandText").GetString());
                Assert.Equal(hex, json.RootElement.GetProperty("SavedCommands")[1].GetProperty("CommandText").GetString());
            }
            var loaded = await service.LoadAsync(path, default);
            Assert.Equal("Terminal", loaded.SavedCommands[0].Name);
            Assert.Equal(TerminalPayload.Trim(), loaded.SavedCommands[2].Name);
            Assert.Equal(TerminalPayload, loaded.SavedCommands[2].CommandText);
            Assert.Equal(Encoding.UTF8.GetBytes(TerminalPayload + suffix), TxPayloadEncoder.Encode(loaded.SavedCommands[0].CommandText, TxSendMode.Terminal, ending));
            Assert.Equal(AllBytes(), TxPayloadEncoder.Encode(loaded.SavedCommands[1].CommandText, TxSendMode.Hex, ending));
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its private directory.");
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(TerminalPayload)]
    [InlineData("\t한글 🙂\t ")]
    [InlineData("\r\n ECHO value\r\n")]
    public void QuickCreateAndEditValidationPreservesPayloadAndDoesNotMutateSource(string text)
    {
        var source = new TxCommand("  Button label  ", text) { OptionalShortcut = " ctrl + 1 " };
        var arguments = new object?[] { source, null, null };
        var method = typeof(MainViewModel).GetMethod("TryNormalizeSavedCommand", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)method.Invoke(null, arguments)!);
        var normalized = Assert.IsType<TxCommand>(arguments[1]);
        Assert.Equal(text, normalized.CommandText);
        Assert.Equal(text, source.CommandText);
        Assert.Equal("Button label", normalized.Name);
        Assert.Equal("Ctrl+1", normalized.OptionalShortcut);
    }

    [Theory]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.None, "")]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.Cr, "\r")]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.Lf, "\n")]
    [InlineData(TxSendMode.Terminal, TxLineEndingMode.Crlf, "\r\n")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.None, "")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.Cr, "")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.Lf, "")]
    [InlineData(TxSendMode.Hex, TxLineEndingMode.Crlf, "")]
    public async Task SharedManualAndQuickSendPathPassesExactBytesToWriterOnce(TxSendMode mode, TxLineEndingMode ending, string suffix)
    {
        using var stop = new CancellationTokenSource();
        var serial = DispatchProxy.Create<ISerialService, RecordingWriter>();
        var writer = (RecordingWriter)serial;
        writer.Stop = stop;
        await using var bridge = new SerialBridgeService();
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_serialService", serial);
        Set(model, "_bridgeService", bridge);
        Set(model, "_automationControl", new AutomationControlGate());
        Set(model, "_isConnected", true);
        Set(model, "_currentUiSettings", new UiSettings { TxSendMode = mode });
        Set(model, "_selectedTxLineEnding", ending);
        var command = new TxCommand("Quick/manual", mode == TxSendMode.Hex ? HexPayload() : TerminalPayload);
        var method = typeof(MainViewModel).GetMethod("SendCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Stop at the serial writer boundary so the test does not need a XAML
        // dispatcher, create a GUI, write a user profile, or start background UI work.
        // This exercises the actual shared send method with its ordinary GUI flags.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (Task<bool>)method.Invoke(model,
            [command, false, null, stop.Token, false, false, false])!);
        Assert.Equal(mode == TxSendMode.Hex ? AllBytes() : Encoding.UTF8.GetBytes(TerminalPayload + suffix), Assert.Single(writer.Writes));
        Assert.Equal(0, model.TxErrorCount);
        Assert.Equal(0, typeof(MainViewModel).GetField("_activeGuiSends", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(model));
    }

    [Theory]
    [InlineData(TxSendMode.Terminal, "text", "crlf")]
    [InlineData(TxSendMode.Hex, "hex", "none")]
    public async Task McpSavedCommandListingKeepsTheStoredPayload(TxSendMode mode, string expectedMode, string expectedEnding)
    {
        var model = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(model, "_startupProfileTask", Task.CompletedTask);
        Set(model, "_currentUiSettings", new UiSettings { TxSendMode = mode });
        Set(model, "_selectedTxLineEnding", TxLineEndingMode.Crlf);
        var commands = new CommandViewModel();
        commands.SavedCommands.Add(new("Exact", TerminalPayload));
        Set(model, "<Commands>k__BackingField", commands);
        var method = typeof(MainViewModel).GetMethod("InvokeAutomationOnUiAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<object>)method.Invoke(model, ["list_commands", JsonSerializer.SerializeToElement(new { }), CancellationToken.None])!;
        var command = Assert.Single(Assert.IsType<AutomationCommandPage>(result).Commands);
        Assert.Equal(TerminalPayload, command.Command);
        Assert.Equal(expectedMode, command.TxMode);
        Assert.Equal(expectedEnding, command.LineEnding);
    }

    private static byte[] AllBytes() => Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    private static string HexPayload() => " \t" + string.Join(" ", AllBytes().Select(value => value.ToString("X2"))) + "\r\n ";
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    public class RecordingWriter : DispatchProxy
    {
        public CancellationTokenSource Stop = null!;
        public readonly List<byte[]> Writes = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != "SendBytesAsync") throw new InvalidOperationException("Unexpected serial call: " + method.Name);
            Writes.Add(((byte[])args![0]!).ToArray());
            Stop.Cancel();
            return Task.FromCanceled((CancellationToken)args[2]!);
        }
    }
}
