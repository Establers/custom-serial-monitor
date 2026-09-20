using System.Diagnostics;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class UpdateConcurrencyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "UpdateConcurrency-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_directory, "updates.json");

    public UpdateConcurrencyTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task MetadataFromStaleWindow_PreservesOtherWindowsChoices()
    {
        var first = new UpdateService(FilePath);
        var second = new UpdateService(FilePath);
        Assert.True((await second.LoadAsync(default)).Automatic);
        await first.ApplyAsync(new(Automatic: false, SkippedVersion: "1.4.0.0"), default);
        await second.ApplyAsync(new(LastSuccessUtc: DateTimeOffset.UtcNow, LastKnownTag: "v1.5.0"), default);
        var saved = await first.LoadAsync(default);
        Assert.False(saved.Automatic);
        Assert.Equal("1.4.0.0", saved.SkippedVersion);
        Assert.Equal("v1.5.0", saved.LastKnownTag);
    }

    [Fact]
    public async Task ThreeProcesses_WritingSimultaneously_PreserveAllFieldsAndCleanTemporaryFiles()
    {
        using var automatic = StartWorker("automatic");
        using var skip = StartWorker("skip");
        using var cache = StartWorker("cache");
        await Task.WhenAll(WaitForSuccess(automatic), WaitForSuccess(skip), WaitForSuccess(cache));
        var saved = await new UpdateService(FilePath).LoadAsync(default);
        Assert.False(saved.Automatic);
        Assert.Equal("1.4.0.0", saved.SkippedVersion);
        Assert.Equal("v1.5.0", saved.LastKnownTag);
        Assert.NotNull(saved.LastSuccessUtc);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task OtherProcessHoldingLock_DoesNotBlockCaller_AndTimesOut()
    {
        using var holder = StartWorker("hold");
        Assert.Equal("LOCKED", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var pending = new UpdateService(FilePath).ApplyAsync(new(Automatic: false), default);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Storage call blocked its caller.");
            Assert.False(pending.IsCompleted);
            await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.False(File.Exists(FilePath));
        }
        finally { await holder.StandardInput.WriteLineAsync("release"); await WaitForSuccess(holder); }
        await new UpdateService(FilePath).ApplyAsync(new(Automatic: false), default);
        Assert.False((await new UpdateService(FilePath).LoadAsync(default)).Automatic);
    }

    [Fact]
    public async Task ShutdownCancelsLockWaitWithoutWriting()
    {
        using var holder = StartWorker("hold");
        Assert.Equal("LOCKED", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        try
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new UpdateService(FilePath).ApplyAsync(new(Automatic: false), cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(File.Exists(FilePath));
        }
        finally { await holder.StandardInput.WriteLineAsync("release"); await WaitForSuccess(holder); }
    }

    [Fact]
    public async Task CrashedLockOwner_DoesNotPreventFutureWrites()
    {
        using var holder = StartWorker("hold");
        Assert.Equal("LOCKED", await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        holder.Kill();
        await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await new UpdateService(FilePath).ApplyAsync(new(Automatic: false), default);
        Assert.False((await new UpdateService(FilePath).LoadAsync(default)).Automatic);
    }

    [Fact]
    public async Task OlderRequestFinishingLast_DoesNotRewindCache()
    {
        var service = new UpdateService(FilePath);
        var now = DateTimeOffset.UtcNow;
        await service.ApplyAsync(new(LastSuccessUtc: now, LastKnownTag: "v1.5.0"), default);
        await service.ApplyAsync(new(LastSuccessUtc: now.AddMinutes(-1), LastKnownTag: "v1.4.0"), default);
        Assert.Equal("v1.5.0", (await service.LoadAsync(default)).LastKnownTag);
    }

    [Fact]
    public async Task CorruptFile_IsNotSilentlyOverwrittenWithDefaults()
    {
        await File.WriteAllTextAsync(FilePath, "broken");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() =>
            new UpdateService(FilePath).ApplyAsync(new(LastAttemptUtc: DateTimeOffset.UtcNow), default));
        Assert.Equal("broken", await File.ReadAllTextAsync(FilePath));
    }

    private Process StartWorker(string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
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
