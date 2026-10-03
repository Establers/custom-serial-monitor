using System.Diagnostics;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class PortAliasServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PortAlias-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "port-aliases.json");

    public PortAliasServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task SeparateLaunchesRestoreNamesByPortAndRemovingOnePreservesTheOther()
    {
        await new PortAliasService(FilePath).SetAsync(" com3 ", " 메인 보드 ", default);
        var staleWindow = new PortAliasService(FilePath);
        await staleWindow.LoadAsync(default);
        await new PortAliasService(FilePath).SetAsync("COM7", "Module", default);
        await staleWindow.SetAsync("com3", "Updated board", default);
        var restored = await new PortAliasService(FilePath).LoadAsync(default);
        Assert.Equal("Updated board", restored["COM3"]);
        Assert.Equal("Module", restored["COM7"]);
        await staleWindow.SetAsync("COM3", " ", default);
        restored = await new PortAliasService(FilePath).LoadAsync(default);
        Assert.False(restored.ContainsKey("COM3"));
        Assert.Equal("Module", restored["COM7"]);
    }

    [Fact]
    public async Task TwoProcessesSavingNamesPreserveBothPortsAndCleanTemporaryFiles()
    {
        using var first = StartWorker("alias-first");
        using var second = StartWorker("alias-second");
        await Task.WhenAll(WaitForSuccess(first), WaitForSuccess(second));
        var saved = await new PortAliasService(FilePath).LoadAsync(default);
        Assert.Equal("Main board", saved["COM3"]);
        Assert.Equal("Module", saved["COM7"]);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task LockWaitDoesNotBlockCallerAndCanBeCanceledWithoutChangingTheFile()
    {
        var service = new PortAliasService(FilePath);
        await service.SetAsync("COM3", "Original", default);
        var original = await File.ReadAllTextAsync(FilePath);
        using var fileLock = new FileStream(FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        var saving = service.SetAsync("COM7", "Other", cancellation.Token);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Storage blocked the caller.");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"COM3\":null}")]
    [InlineData("[]")]
    [InlineData("{\"COM3\":123}")]
    [InlineData("{\"COM3\":true}")]
    [InlineData("{\"COM3\":[\"Board\"]}")]
    [InlineData("{\"COM3\":\"Board\\u0000\"}")]
    [InlineData("{\"COM3\":\"Board\\uD800\"}")]
    [InlineData("{\"COM3\":\"Board\",\"COM7\":\"Module\"")]
    [InlineData("{\"COM3\":\"first\",\"COM3\":\"second\"}")]
    [InlineData("{\"COM3\":\"first\",\"com3\":\"second\"}")]
    public async Task DamagedFileIsReportedAndNeverOverwritten(string content)
    {
        await File.WriteAllTextAsync(FilePath, content);
        await Assert.ThrowsAnyAsync<Exception>(() => new PortAliasService(FilePath).SetAsync("COM7", "Module", default));
        Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\tsecond")]
    [InlineData("first\u2028second")]
    [InlineData("first\u2029second")]
    [InlineData("first\0second")]
    [InlineData("first\u001Bsecond")]
    [InlineData("first\u202Esecond")]
    [InlineData("first\u2066second")]
    public async Task NamesCannotInjectLinesOrControlsIntoTheWindowTitle(string alias) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new PortAliasService(FilePath).SetAsync("COM3", alias, default));

    [Fact]
    public async Task UnpairedSurrogatesAreRejectedWithoutCreatingAFile()
    {
        foreach (var alias in new[] { "Board" + (char)0xD800, "Board" + (char)0xDC00,
            "Board" + (char)0xD800 + "x" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new PortAliasService(FilePath).SetAsync("COM3", alias, default));
            Assert.False(File.Exists(FilePath));
        }
    }

    [Theory]
    [InlineData("메인 보드 🔧")]
    [InlineData("emoji 👩‍💻")]
    [InlineData("{0} \"quoted\" <xml>&' text")]
    [InlineData("<script>alert('test')</script>")]
    [InlineData("../logs/board:?#%")]
    [InlineData("$(noop) `literal` %LOCALAPPDATA%")]
    public async Task UnicodeAndPunctuationRemainLiteralAndRoundTripExactly(string name)
    {
        await new PortAliasService(FilePath).SetAsync("COM3", name, default);
        Assert.Equal(name, (await new PortAliasService(FilePath).LoadAsync(default))["COM3"]);
        Assert.Equal(2, Directory.GetFiles(_directory).Length); // Alias JSON and its lock only.
    }

    [Fact]
    public async Task SixtyFourCharactersAreAcceptedAndSixtyFiveDoNotChangeTheSavedName()
    {
        var service = new PortAliasService(FilePath);
        var boundaryName = new string('a', 62) + "🔧";
        await service.SetAsync("COM3", boundaryName, default);
        var original = await File.ReadAllTextAsync(FilePath);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync("COM3", boundaryName + "x", default));
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        Assert.Equal(boundaryName, (await service.LoadAsync(default))["COM3"]);
    }

    [Fact]
    public async Task OversizedFilesAndTooManyEntriesAreRejectedWithoutOverwriting()
    {
        var service = new PortAliasService(FilePath);
        foreach (var content in new[]
        {
            "{\"COM3\":\"Board\"}" + new string(' ', 256 * 1024),
            System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(1, 513)
                .ToDictionary(index => "COM" + index, _ => "Board"))
        })
        {
            await File.WriteAllTextAsync(FilePath, content);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.LoadAsync(default));
            await Assert.ThrowsAsync<InvalidDataException>(() => service.SetAsync("COM7", "Module", default));
            Assert.Equal(content, await File.ReadAllTextAsync(FilePath));
        }
    }

    [Fact]
    public async Task FullPortLimitStillAllowsUpdatingOrRemovingAnExistingName()
    {
        var initial = Enumerable.Range(1, 512).ToDictionary(index => "COM" + index, _ => "Board");
        await File.WriteAllTextAsync(FilePath, System.Text.Json.JsonSerializer.Serialize(initial));
        var service = new PortAliasService(FilePath);
        await service.SetAsync("COM3", "Updated", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetAsync("COM999", "Overflow", default));
        var saved = await service.LoadAsync(default);
        Assert.Equal(512, saved.Count);
        Assert.Equal("Updated", saved["COM3"]);
        await service.SetAsync("COM3", string.Empty, default);
        await service.SetAsync("COM999", "Replacement", default);
        Assert.Equal(512, (await service.LoadAsync(default)).Count);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task HeldStorageLockTimesOutAndRecoversWithoutChangingSavedData()
    {
        var service = new PortAliasService(FilePath);
        await service.SetAsync("COM3", "Original", default);
        var original = await File.ReadAllTextAsync(FilePath);
        using (var fileLock = new FileStream(FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => service.SetAsync("COM7", "Module", default)
                .WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        }
        await service.SetAsync("COM7", "Module", default);
        Assert.Equal("Module", (await service.LoadAsync(default))["COM7"]);
    }

    private Process StartWorker(string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "UpdateTestWorker", "SerialMonitor.UpdateTestWorker.dll"));
        start.ArgumentList.Add(FilePath);
        start.ArgumentList.Add(mode);
        return Process.Start(start)!;
    }

    private static async Task WaitForSuccess(Process process)
    {
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
