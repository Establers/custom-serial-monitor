using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class FileLogStatusTests
{
    [Fact]
    public void IntentionalPause_IsNotReportedAsSaving()
    {
        Assert.Equal("PAUSED", FileLogStatus.Describe(true, FileLogWriterState.Running, true, false, paused: true));
    }

    [Theory]
    [InlineData(true, FileLogWriterState.Stopped, false, false, "WAITING")]
    [InlineData(true, FileLogWriterState.Starting, true, false, "STARTING")]
    [InlineData(true, FileLogWriterState.Running, false, false, "WAITING")]
    [InlineData(true, FileLogWriterState.Running, true, false, "ON")]
    [InlineData(true, FileLogWriterState.Running, true, true, "WARN")]
    [InlineData(true, FileLogWriterState.Faulted, true, true, "FAULT")]
    [InlineData(false, FileLogWriterState.Faulted, false, true, "FAULT")]
    [InlineData(false, FileLogWriterState.Stopping, true, false, "STOPPING")]
    [InlineData(false, FileLogWriterState.Stopped, false, false, "OFF")]
    public void StatusReflectsWriterState(bool enabled, FileLogWriterState state, bool hasFile, bool hasError, string expected)
    {
        Assert.Equal(expected, FileLogStatus.Describe(enabled, state, hasFile, hasError));
    }
}
