using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class SerialBaudRateUpdateTests
{
    [Fact]
    public void SuccessfulChangeUsesDriverReadback()
    {
        var driverBaud = 115200;
        var reads = 0;
        var applied = SerialBaudRateUpdate.Apply(57600,
            () => { reads++; return driverBaud; }, value => driverBaud = value);

        Assert.Equal(57600, applied);
        Assert.Equal(57600, driverBaud);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void FailureAfterStagingSettingRestoresOriginalDriverValue()
    {
        var stagedBaud = 115200;
        var driverBaud = 115200;
        var writes = new List<int>();
        var error = Assert.Throws<SerialBaudRateUpdateException>(() => SerialBaudRateUpdate.Apply(57600,
            () => driverBaud,
            value =>
            {
                writes.Add(value);
                stagedBaud = value;
                if (value == 57600) throw new IOException("SetCommState failed");
                driverBaud = value;
            }));

        Assert.True(error.StateRestored);
        Assert.Equal(new[] { 57600, 115200 }, writes);
        Assert.Equal(115200, stagedBaud);
        Assert.Equal(115200, driverBaud);
    }

    [Fact]
    public void DriverIgnoringRequestedBaudIsNotReportedAsSuccess()
    {
        var writes = new List<int>();
        var error = Assert.Throws<SerialBaudRateUpdateException>(() => SerialBaudRateUpdate.Apply(57600,
            () => 115200, value => writes.Add(value)));

        Assert.True(error.StateRestored);
        Assert.Equal(new[] { 57600, 115200 }, writes);
        Assert.Contains("reported 115200", error.Message);
    }

    [Fact]
    public void FailedRestoreExposesUncertainDriverState()
    {
        var error = Assert.Throws<SerialBaudRateUpdateException>(() => SerialBaudRateUpdate.Apply(57600,
            () => 115200, _ => throw new IOException("driver unavailable")));

        Assert.False(error.StateRestored);
        Assert.IsType<AggregateException>(error.InnerException);
    }
}
