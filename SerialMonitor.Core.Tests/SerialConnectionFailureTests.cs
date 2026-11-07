using System.ComponentModel;
using SerialMonitor.Core.Diagnostics;

namespace SerialMonitor.Core.Tests;

public sealed class SerialConnectionFailureTests
{
    [Theory]
    [InlineData(5, SerialConnectFailureKind.AccessDenied)]
    [InlineData(32, SerialConnectFailureKind.PortInUse)]
    [InlineData(33, SerialConnectFailureKind.PortInUse)]
    [InlineData(2, SerialConnectFailureKind.PortNotFound)]
    [InlineData(3, SerialConnectFailureKind.PortNotFound)]
    [InlineData(6, SerialConnectFailureKind.DeviceUnavailable)]
    [InlineData(21, SerialConnectFailureKind.DeviceUnavailable)]
    [InlineData(31, SerialConnectFailureKind.DeviceUnavailable)]
    [InlineData(1167, SerialConnectFailureKind.DeviceUnavailable)]
    [InlineData(121, SerialConnectFailureKind.TimedOut)]
    [InlineData(1460, SerialConnectFailureKind.TimedOut)]
    [InlineData(87, SerialConnectFailureKind.InvalidSettings)]
    public void NativeCode_OverridesWrapperAndMessage(int code, SerialConnectFailureKind expected)
    {
        var result = SerialConnectionFailureClassifier.Classify(new InvalidOperationException("Failed to open COM7", new Win32Exception(code, "번역된 운영체제 오류")));
        Assert.Equal(expected, result.Kind); Assert.Equal(code, result.NativeErrorCode);
        Assert.Contains("번역된 운영체제 오류", result.TechnicalMessage);
    }

    [Fact]
    public void UnauthorizedAccess_AloneIsNotProofOfPortOwnership()
    {
        var result = SerialConnectionFailureClassifier.Classify(new UnauthorizedAccessException("fixture"));
        Assert.Equal(SerialConnectFailureKind.AccessDenied, result.Kind);
        Assert.Equal(5, result.NativeErrorCode); Assert.True(result.NeedsOwnerLookup);
    }

    [Fact]
    public void FileNotFoundAndInvalidSettings_DoNotNeedOwnerScanning()
    {
        var missing = SerialConnectionFailureClassifier.Classify(new InvalidOperationException("wrapper", new FileNotFoundException("fixture")));
        Assert.Equal(SerialConnectFailureKind.PortNotFound, missing.Kind); Assert.False(missing.NeedsOwnerLookup);
        Assert.False(SerialConnectionFailureClassifier.Classify(new ArgumentException("baud")).NeedsOwnerLookup);
        Assert.False(SerialConnectionFailureClassifier.Classify(new TimeoutException()).NeedsOwnerLookup);
    }

    [Fact]
    public void OriginalError_IsBoundedAndKeepsTheInnerCauseFirst()
    {
        var result = SerialConnectionFailureClassifier.Classify(new Exception(new string('x', 10000), new IOException("root cause")));
        Assert.StartsWith("root cause", result.TechnicalMessage); Assert.True(result.TechnicalMessage.Length <= 2049);
    }

    [Theory]
    [InlineData("COM7", "COM7")]
    [InlineData("com123", "COM123")]
    [InlineData("\\\\.\\COM7", "COM7")]
    [InlineData("CNCA0", "CNCA0")]
    [InlineData("cncb0", "CNCB0")]
    public void PortArguments_AreCanonicalDeviceNames(string value, string expected) => Assert.Equal(expected, PortOwnerProbeProtocol.NormalizePort(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COM7 & whoami")]
    [InlineData("C:\\temp\\a.txt")]
    [InlineData("COM7\n")]
    [InlineData("COM-7")]
    [InlineData("COM123456789")]
    [InlineData("MOCK")]
    public void InvalidPortArguments_AreRejected(string? value) => Assert.Null(PortOwnerProbeProtocol.NormalizePort(value));
}
