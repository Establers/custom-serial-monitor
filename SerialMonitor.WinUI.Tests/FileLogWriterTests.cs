using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogWriterTests
{
    [Fact]
    public async Task StartAsync_WithoutAnyLines_StillCreatesANewTimestampedFile()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();

        await writer.StartAsync(directory.Path, CancellationToken.None);
        await writer.StopAsync(CancellationToken.None);

        var logFile = Assert.Single(Directory.GetFiles(directory.Path, "*.log"));
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}_\d{6}_serial\.log$",
            Path.GetFileName(logFile));
        Assert.Equal(0, new FileInfo(logFile).Length);
    }

    [Fact]
    public async Task OnOffOnCycle_DrainsAcceptedLines_RejectsOffWrites_AndRestartsCleanly()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);

        Assert.True(writer.TryEnqueue(new LogLine(
            DateTimeOffset.Now,
            LogDirection.Rx,
            "READY",
            "READY"u8.ToArray(),
            displayText: "READY",
            contentMode: LogRuleMatchMode.Terminal)));
        Assert.True(writer.TryEnqueue(new LogLine(
            DateTimeOffset.Now,
            LogDirection.Rx,
            "\0?",
            new byte[] { 0x00, 0xFF },
            displayText: "00 FF",
            contentMode: LogRuleMatchMode.Hex)));

        await writer.StopAsync(CancellationToken.None);

        Assert.False(writer.IsRunning);
        Assert.Null(writer.CurrentLogFilePath);
        Assert.NotNull(writer.LastLogFilePath);
        Assert.True(File.Exists(writer.LastLogFilePath));
        Assert.False(writer.TryEnqueue(LogLine.System("after OFF")));
        Assert.Equal(2, writer.WrittenLineCount);

        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.System("after ON again")));
        await writer.StopAsync(CancellationToken.None);
        Assert.Equal(3, writer.WrittenLineCount);

        var logFiles = Directory.GetFiles(directory.Path, "*.log");
        Assert.Equal(2, logFiles.Length);
        Assert.Equal(2, logFiles.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(logFiles, path => Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}_\d{6}_serial(?:_dup\d{3})?\.log$",
            Path.GetFileName(path)));
        var contents = string.Join(
            Environment.NewLine,
            await Task.WhenAll(logFiles.Select(path => File.ReadAllTextAsync(path))));
        var firstRunFile = Assert.Single(logFiles, path => File.ReadAllText(path).Contains("RX < READY", StringComparison.Ordinal));
        var secondRunFile = Assert.Single(logFiles, path => File.ReadAllText(path).Contains("after ON again", StringComparison.Ordinal));
        Assert.NotEqual(firstRunFile, secondRunFile);
        Assert.DoesNotContain("after ON again", await File.ReadAllTextAsync(firstRunFile), StringComparison.Ordinal);
        Assert.DoesNotContain("RX < READY", await File.ReadAllTextAsync(secondRunFile), StringComparison.Ordinal);
        Assert.Contains("RX < READY", contents, StringComparison.Ordinal);
        Assert.Contains("RX < 00 FF", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("after OFF", contents, StringComparison.Ordinal);
        Assert.Contains("after ON again", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), path =>
            path.EndsWith("_events.log", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplicitLogFileName_IsUsedExactlyWithoutAutomaticPrefixOrSuffix()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("bench (A) #1.txt", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        Assert.True(writer.TryEnqueue(LogLine.Mark("exact name")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal("bench (A) #1.txt", Path.GetFileName(file));
        Assert.Contains("exact name", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("folder/capture.log")]
    [InlineData("folder\\capture.log")]
    [InlineData("CON.log")]
    [InlineData("capture.")]
    public async Task InvalidExplicitLogFileName_IsRejectedWithoutCreatingAFile(string fileName)
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();

        Assert.Throws<ArgumentException>(() => writer.UpdateLogFileName(fileName, requestNewFile: false));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExplicitLogFileName_RefusesToReuseAnExistingFile()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = Path.Combine(directory.Path, "capture.log");
        await File.WriteAllTextAsync(existingPath, "keep me");
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("capture.log", requestNewFile: false);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            writer.StartAsync(directory.Path, CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("keep me", await File.ReadAllTextAsync(existingPath));
        Assert.False(writer.IsRunning);
    }

    [Fact]
    public async Task ExplicitLogFileName_DoesNotSplitWhenLogDateChanges()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        writer.UpdateLogFileName("long-run.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var firstDate = new DateTimeOffset(2026, 7, 14, 23, 59, 59, TimeSpan.Zero);
        var secondDate = firstDate.AddDays(1);

        Assert.True(writer.TryEnqueue(new LogLine(firstDate, LogDirection.System, "day one")));
        Assert.True(writer.TryEnqueue(new LogLine(secondDate, LogDirection.System, "day two")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path));
        Assert.Equal("long-run.log", Path.GetFileName(file));
        var contents = await File.ReadAllTextAsync(file);
        Assert.Contains("day one", contents, StringComparison.Ordinal);
        Assert.Contains("day two", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitLogFileName_SizeRotationAvoidsExistingSegmentNames()
    {
        using var directory = new TemporaryDirectory();
        var existingRotationPath = Path.Combine(directory.Path, "capture_001.log");
        await File.WriteAllTextAsync(existingRotationPath, "keep existing segment");
        await using var writer = new FileLogWriter
        {
            MaximumFileSizeBytes = 1
        };
        writer.UpdateLogFileName("capture.log", requestNewFile: false);
        await writer.StartAsync(directory.Path, CancellationToken.None);

        Assert.True(writer.TryEnqueue(LogLine.System("first")));
        Assert.True(writer.TryEnqueue(LogLine.System("second")));
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal("keep existing segment", await File.ReadAllTextAsync(existingRotationPath));
        Assert.True(File.Exists(Path.Combine(directory.Path, "capture.log")));
        var duplicateRotationPath = Path.Combine(directory.Path, "capture_001_dup001.log");
        Assert.True(File.Exists(duplicateRotationPath));
        Assert.Contains("second", await File.ReadAllTextAsync(duplicateRotationPath), StringComparison.Ordinal);
        Assert.Equal(2, writer.WrittenLineCount);
    }

    [Fact]
    public async Task StopAsync_RetainsLastCompletedLogPath()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var activePath = writer.CurrentLogFilePath;

        await writer.StopAsync(CancellationToken.None);

        Assert.NotNull(activePath);
        Assert.Null(writer.CurrentLogFilePath);
        Assert.Equal(activePath, writer.LastLogFilePath);
        Assert.True(File.Exists(writer.LastLogFilePath));
    }

    [Fact]
    public async Task DifferentLogDates_StayInTheSameAutomaticFile()
    {
        using var directory = new TemporaryDirectory();
        await using var writer = new FileLogWriter();
        await writer.StartAsync(directory.Path, CancellationToken.None);
        var firstDate = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var secondDate = firstDate.AddDays(1);

        Assert.True(writer.TryEnqueue(new LogLine(firstDate, LogDirection.System, "day one")));
        Assert.True(writer.TryEnqueue(new LogLine(secondDate, LogDirection.System, "day two")));
        await writer.StopAsync(CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(directory.Path, "*.log"));
        var contents = await File.ReadAllTextAsync(file);
        Assert.Contains("day one", contents, StringComparison.Ordinal);
        Assert.Contains("day two", contents, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"SerialMonitorTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
