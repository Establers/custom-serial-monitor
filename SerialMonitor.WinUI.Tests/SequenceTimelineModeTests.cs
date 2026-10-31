using System.Text;
using SerialMonitor.Core.Sequences;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Tests;

public sealed class SequenceTimelineModeTests
{
    [Theory]
    [InlineData("Hex", "text", true)]
    [InlineData("HEX", "text", true)]
    [InlineData("Terminal", "hex", false)]
    [InlineData("text", "hex", false)]
    [InlineData(null, "hex", true)]
    [InlineData(null, "text", false)]
    public void LogAutomaticallyUsesTheRecordedModeWithoutASeparateViewSetting(string? mode, string kind, bool hex)
    {
        var time = DateTimeOffset.Now;
        var step = new SequenceStepResult(1, 1, 1, 1, "ping", "70696E670D0A", "sent", "matched", "READY", kind,
            50, time, time.AddMilliseconds(50), 10, 0, 5, 5, 0, 5, null, TxMode: mode);
        var lines = SequenceTimelineFormatter.Format(step, [new(0, Encoding.UTF8.GetBytes("READY"), time.AddMilliseconds(10))], Encoding.UTF8);

        Assert.Equal(hex ? "70 69 6E 67 0D 0A" : "ping", Assert.Single(lines[0].Parts).Text);
        Assert.Equal(hex ? "52 45 41 44 59" : "READY", string.Concat(lines[1].Parts.Select(part => part.Text)).TrimEnd());
        Assert.All(lines[1].Parts, part => Assert.True(part.Matched));
        Assert.Equal(time.AddMilliseconds(10).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), lines[1].Time);
    }
}
