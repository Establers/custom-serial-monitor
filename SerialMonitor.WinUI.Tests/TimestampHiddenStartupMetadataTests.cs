using System.Globalization;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;
using SerialMonitor.WinUI.ViewModels;

namespace SerialMonitor.WinUI.Tests;

public sealed class TimestampHiddenStartupMetadataTests
{
    [Theory]
    [InlineData(RxDisplayMode.Terminal, false)]
    [InlineData(RxDisplayMode.Terminal, true)]
    [InlineData(RxDisplayMode.Hex, false)]
    [InlineData(RxDisplayMode.Hex, true)]
    public async Task SavedHiddenTimestampProfile_EmitsMetadataInTheVeryFirstLiveBatch(
        RxDisplayMode mode, bool showDirection)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SerialMonitor.TimestampStartupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profile.json");
            var service = new ProfileService();
            var profile = service.CreateDefaultProfile();
            profile.UiSettings.ShowTimestampInLogView = false;
            profile.UiSettings.ShowRxTxDirectionPrefixInLogView = showDirection;
            profile.UiSettings.TimestampDisplayFormat = TimestampDisplayFormat.TimeSeconds;
            profile.UiSettings.RxDisplayMode = mode;
            await service.SaveAsync(path, profile, CancellationToken.None);
            var restored = await new ProfileService().LoadAsync(path, CancellationToken.None);
            Assert.False(restored.UiSettings.ShowTimestampInLogView);

            // Mirror the LogViewModel calls made by MainViewModel.ApplyProfile
            // before any setting is manually toggled or any first RX arrives.
            var model = new LogViewModel(restored.UiSettings.MaxVisibleLogLines);
            model.SetCapacity(restored.UiSettings.MaxVisibleLogLines);
            model.SetShowTimestampInLogView(restored.UiSettings.ShowTimestampInLogView);
            model.SetShowRxTxDirectionPrefixInLogView(restored.UiSettings.ShowRxTxDirectionPrefixInLogView);
            model.SetTimestampDisplayFormat(restored.UiSettings.TimestampDisplayFormat);
            model.SetRxDisplayMode(restored.UiSettings.RxDisplayMode, clearExisting: true);
            var batches = new List<LogTextBatch>();
            model.TextBatchAppended += (_, batch) => batches.Add(batch);
            var timestamp = new DateTimeOffset(2026, 10, 28, 14, 32, 10, 123, TimeSpan.Zero);

            model.AddRange([new LogLine(timestamp, LogDirection.Rx, "READY", "READY"u8.ToArray())]);

            var batch = Assert.Single(batches);
            var identity = Identity(1, timestamp);
            Assert.StartsWith(identity, batch.AppendedText);
            Assert.StartsWith(identity, model.GetXtermTextSnapshot());
            Assert.DoesNotContain("14:32:10", batch.AppendedText);
            Assert.DoesNotContain("14:32:10", model.GetVisibleTextSnapshot());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void HiddenFromStartup_PartialRxKeepsFirstTimestampInLiveAndRestoredText()
    {
        var model = new LogViewModel(100);
        model.SetShowTimestampInLogView(false);
        model.SetShowRxTxDirectionPrefixInLogView(false);
        var batches = new List<LogTextBatch>();
        model.TextBatchAppended += (_, batch) => batches.Add(batch);
        var first = new DateTimeOffset(2026, 10, 28, 14, 32, 10, 123, TimeSpan.Zero);

        model.AddRange([new LogLine(first, LogDirection.Rx, "AL", isPartialRxSegment: true)]);
        model.AddRange([new LogLine(first.AddMilliseconds(50), LogDirection.Rx, "IVE", isPartialRxSegment: true)]);
        model.AddRange([new LogLine(first.AddMilliseconds(100), LogDirection.Rx, string.Empty, isPartialRxTerminator: true)]);
        model.AddRange([new LogLine(first.AddSeconds(1), LogDirection.Rx, "NEXT")]);

        var liveText = string.Concat(batches.Select(batch => batch.AppendedText));
        var restoredText = model.GetXtermTextSnapshot();
        Assert.Contains(Identity(1, first) + "ALIVE", liveText);
        Assert.Contains(Identity(1, first) + "ALIVE", restoredText);
        Assert.Contains(Identity(4, first.AddSeconds(1)), liveText);
        Assert.Contains(Identity(4, first.AddSeconds(1)), restoredText);
        Assert.DoesNotContain(Identity(2, first.AddMilliseconds(50)), liveText);
    }

    [Fact]
    public void AFirstVisibleToggleDoesNotIntroduceMissingMetadataIntoLaterBatches()
    {
        var model = new LogViewModel(100);
        model.SetShowTimestampInLogView(false);
        var timestamp = new DateTimeOffset(2026, 10, 28, 14, 32, 10, 123, TimeSpan.Zero);
        var batches = new List<LogTextBatch>();
        model.TextBatchAppended += (_, batch) => batches.Add(batch);

        model.AddRange([new LogLine(timestamp, LogDirection.Rx, "FIRST HIDDEN")]);
        model.SetShowTimestampInLogView(true);
        model.AddRange([new LogLine(timestamp.AddSeconds(1), LogDirection.Rx, "FIRST SHOWN")]);
        model.SetShowTimestampInLogView(false);
        model.AddRange([new LogLine(timestamp.AddSeconds(2), LogDirection.Rx, "HIDDEN AGAIN")]);

        Assert.Equal(3, batches.Count);
        for (var index = 0; index < batches.Count; index++)
            Assert.StartsWith(Identity(index + 1, timestamp.AddSeconds(index)), batches[index].AppendedText);
    }

    private static string Identity(long id, DateTimeOffset timestamp) =>
        $"\u001b]777;{id.ToString(CultureInfo.InvariantCulture)},{timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}\u0007";
}
