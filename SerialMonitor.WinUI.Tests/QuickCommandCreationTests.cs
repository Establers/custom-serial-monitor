using System.Reflection;
using System.Text;
using System.Text.Json;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class QuickCommandCreationTests
{
    [Fact]
    public async Task TerminalAndHexQuickCommandsCanBeCreatedPersistedAndEncoded()
    {
        var terminal = Normalize(new("  F103RB Quick Terminal  ", "PING QUICK-TERM")
        {
            LineEndingMode = TxLineEndingMode.Crlf, OptionalShortcut = "Ctrl+1"
        });
        byte[] payload = [0, 10, 13, 127, 128, 165, 90, 255];
        byte[] request = Frame(2, 4242, payload);
        byte[] response = Frame(0x82, 4242, payload);
        var hex = Normalize(new("F103RB Quick HEX", Convert.ToHexString(request))
        {
            LineEndingMode = TxLineEndingMode.Crlf, OptionalShortcut = "Ctrl+2"
        });
        Assert.Equal("F103RB Quick Terminal", terminal.Name);
        Assert.Equal("Ctrl+1", terminal.OptionalShortcut);
        Assert.Equal("Ctrl+2", hex.OptionalShortcut);

        // Exercise the real profile writer/reader at a private test-only path.
        // No running GUI, default profile, or user's Quick CMD list is modified.
        string testDirectory = Path.Combine(Path.GetTempPath(), "SerialMonitorQuickCmdTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        string profilePath = Path.Combine(testDirectory, "quick-commands.json");
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.SavedCommands = [terminal, hex];
        await service.SaveAsync(profilePath, profile, CancellationToken.None);
        var loaded = await service.LoadAsync(profilePath, CancellationToken.None);
        Assert.Equal(2, loaded.SavedCommands.Count);
        var savedTerminal = loaded.SavedCommands[0];
        var savedHex = loaded.SavedCommands[1];
        Assert.Equal(terminal.Name, savedTerminal.Name);
        Assert.Equal(terminal.CommandText, savedTerminal.CommandText);
        Assert.Equal(terminal.OptionalShortcut, savedTerminal.OptionalShortcut);
        Assert.Equal(hex.Name, savedHex.Name);
        Assert.Equal(hex.CommandText, savedHex.CommandText);
        Assert.Equal(hex.OptionalShortcut, savedHex.OptionalShortcut);

        byte[] terminalBytes = TxPayloadEncoder.Encode(savedTerminal.CommandText, TxSendMode.Terminal, savedTerminal.LineEndingMode!.Value);
        Assert.Equal(Encoding.UTF8.GetBytes("PING QUICK-TERM\r\n"), terminalBytes);
        foreach (var ending in Enum.GetValues<TxLineEndingMode>())
            Assert.Equal(request, TxPayloadEncoder.Encode(savedHex.CommandText, TxSendMode.Hex, ending));

        string? exportPath = Environment.GetEnvironmentVariable("SERIAL_LAB_QUICKCMD_CASES");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(exportPath))!);
            var cases = new[]
            {
                new { name = savedTerminal.Name, command = savedTerminal.CommandText, txMode = "text", lineEnding = "crlf",
                    txHex = Convert.ToHexString(terminalBytes), expectedRxHex = Convert.ToHexString(Encoding.UTF8.GetBytes("PONG QUICK-TERM\r\n")) },
                new { name = savedHex.Name, command = savedHex.CommandText, txMode = "hex", lineEnding = "none",
                    txHex = Convert.ToHexString(request), expectedRxHex = Convert.ToHexString(response) }
            };
            await File.WriteAllTextAsync(exportPath, JsonSerializer.Serialize(new { profilePath, cases }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData("", "PING")]
    [InlineData("   ", "PING")]
    [InlineData("Name", "")]
    [InlineData("Name", "   ")]
    public void EmptyNameOrPayloadIsRejected(string name, string command)
    {
        var arguments = new object?[] { new TxCommand(name, command), null, null };
        Assert.False((bool)Normalizer().Invoke(null, arguments)!);
        Assert.False(string.IsNullOrWhiteSpace((string?)arguments[2]));
    }

    [Fact]
    public void UnsupportedShortcutIsRejected()
    {
        var arguments = new object?[] { new TxCommand("Bad shortcut", "PING") { OptionalShortcut = "Ctrl+Alt+F1" }, null, null };
        Assert.False((bool)Normalizer().Invoke(null, arguments)!);
    }

    [Fact]
    public void QuickCommandUsesActiveTxModeRatherThanStoringItsOwnMode()
    {
        var command = Normalize(new("Mode-sensitive", "41 42") { LineEndingMode = TxLineEndingMode.Crlf });
        Assert.Equal(Encoding.UTF8.GetBytes("41 42\r\n"), TxPayloadEncoder.Encode(command.CommandText, TxSendMode.Terminal, command.LineEndingMode!.Value));
        Assert.Equal(new byte[] { 0x41, 0x42 }, TxPayloadEncoder.Encode(command.CommandText, TxSendMode.Hex, command.LineEndingMode.Value));
    }

    [Fact]
    public void InvalidHexQuickCommandCannotBecomeReplacementBytes()
    {
        var command = Normalize(new("Invalid HEX", "GG"));
        Assert.ThrowsAny<ArgumentException>(() => TxPayloadEncoder.Encode(command.CommandText, TxSendMode.Hex, TxLineEndingMode.None));
    }

    private static TxCommand Normalize(TxCommand source)
    {
        var arguments = new object?[] { source, null, null };
        Assert.True((bool)Normalizer().Invoke(null, arguments)!, (string?)arguments[2]);
        return Assert.IsType<TxCommand>(arguments[1]);
    }
    private static MethodInfo Normalizer() => typeof(MainViewModel).GetMethod("TryNormalizeSavedCommand", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static byte[] Frame(byte type, uint sequence, byte[] payload)
    {
        byte[] result = new byte[payload.Length + 11];
        result[0] = 0xa5; result[1] = 0x5a; result[2] = type;
        for (int i = 0; i < 4; ++i) result[3 + i] = (byte)(sequence >> (8 * i));
        result[7] = (byte)payload.Length; result[8] = (byte)(payload.Length >> 8);
        payload.CopyTo(result, 9);
        ushort crc = 0xffff;
        for (int i = 2; i < result.Length - 2; ++i)
        {
            crc ^= (ushort)(result[i] << 8);
            for (int bit = 0; bit < 8; ++bit) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        result[^2] = (byte)crc; result[^1] = (byte)(crc >> 8);
        return result;
    }
}
