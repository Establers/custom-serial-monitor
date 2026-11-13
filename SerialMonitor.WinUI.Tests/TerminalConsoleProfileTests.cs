using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class TerminalConsoleProfileTests
{
    [Theory]
    [InlineData(TerminalEnterMode.Lf, TerminalBackspaceMode.Bs, TerminalReceiveNewlineMode.Auto)]
    [InlineData(TerminalEnterMode.CrLf, TerminalBackspaceMode.Del, TerminalReceiveNewlineMode.Cr)]
    [InlineData(TerminalEnterMode.Cr, TerminalBackspaceMode.Bs, TerminalReceiveNewlineMode.Lf)]
    public async Task ConsolePreferencesSurviveCloneAndActualProfileSaveLoad(
        TerminalEnterMode enter, TerminalBackspaceMode backspace, TerminalReceiveNewlineMode receive)
    {
        using var temporary = new TemporaryProfile();
        var service = new ProfileService();
        var profile = service.CreateDefaultProfile();
        profile.UiSettings.TerminalLocalEcho = false;
        profile.UiSettings.TerminalEnter = enter;
        profile.UiSettings.TerminalBackspace = backspace;
        profile.UiSettings.TerminalReceiveNewline = receive;
        profile.UiSettings = profile.UiSettings.Clone();
        await service.SaveAsync(temporary.Path, profile, CancellationToken.None);
        var loaded = await service.LoadAsync(temporary.Path, CancellationToken.None);
        Assert.False(loaded.UiSettings.TerminalLocalEcho);
        Assert.Equal(enter, loaded.UiSettings.TerminalEnter);
        Assert.Equal(backspace, loaded.UiSettings.TerminalBackspace);
        Assert.Equal(receive, loaded.UiSettings.TerminalReceiveNewline);
        Assert.Null(service.LastError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"UiSettings\":{\"TerminalEnter\":999,\"TerminalBackspace\":-1,\"TerminalReceiveNewline\":999}}")]
    public async Task OldProfilesAndInvalidOptionsKeepTheOriginalWireAndDisplayDefaults(string json)
    {
        using var temporary = new TemporaryProfile();
        await File.WriteAllTextAsync(temporary.Path, json);
        var loaded = await new ProfileService().LoadAsync(temporary.Path, CancellationToken.None);
        Assert.True(loaded.UiSettings.TerminalLocalEcho);
        Assert.Equal(TerminalEnterMode.Cr, loaded.UiSettings.TerminalEnter);
        Assert.Equal(TerminalBackspaceMode.Del, loaded.UiSettings.TerminalBackspace);
        Assert.Equal(TerminalReceiveNewlineMode.Preserve, loaded.UiSettings.TerminalReceiveNewline);
    }

    private sealed class TemporaryProfile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("SerialMonitor.ConsoleSettings.").FullName;
        public string Path => System.IO.Path.Combine(_directory, "profile.json");
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
