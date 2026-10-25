using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationControlTests
{
    private static AutomationDeviceState State(bool connected, string port = "COM7", int baud = 115200) =>
        new(connected, port, baud, 1, "Utf8", 0, false, null);

    [Fact]
    public async Task BridgeStartHoldsControlAcrossPendingPortOpen()
    {
        var gate = new AutomationControlGate();
        var portOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task StartBridge()
        {
            using var startup = gate.TryBeginBridgeStart();
            Assert.NotNull(startup);
            await portOpen.Task;
        }
        var bridge = StartBridge();
        Assert.True(gate.IsBridgeStarting);
        Assert.False(gate.TryReserveAutomation());
        Assert.Null(gate.TryBeginBridgeStart());
        portOpen.SetResult();
        await bridge.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsBridgeStarting);
        Assert.True(gate.TryReserveAutomation());
        Assert.Null(gate.TryBeginBridgeStart());
        gate.ReleaseAutomation();
        using var nextBridge = gate.TryBeginBridgeStart();
        Assert.NotNull(nextBridge);
    }

    [Fact]
    public async Task FailedBridgeStartupReleasesControl()
    {
        var gate = new AutomationControlGate();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var startup = gate.TryBeginBridgeStart();
            await Task.Yield();
            throw new IOException("Port open failed.");
        });
        Assert.False(gate.IsBridgeStarting);
        Assert.True(gate.TryReserveAutomation());
    }

    [Fact]
    public async Task CancellationDuringSharedPortRefreshNeverSelectsOrConnects()
    {
        using var request = new CancellationTokenSource();
        var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = false;
        var connected = false;
        var operation = AutomationConnectionOperation.ConnectAsync("COM7", 115200, () => State(false),
            () => refresh.Task, () => selected = true,
            _ => { connected = true; return Task.CompletedTask; }, request.Token);
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(selected);
        Assert.False(connected);
        Assert.False(refresh.Task.IsCanceled);
        refresh.SetResult();
        Assert.False(selected);
        Assert.False(connected);
    }

    [Fact]
    public async Task CancellationAtEndOfPortRefreshPreventsConnection()
    {
        using var request = new CancellationTokenSource();
        var connected = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutomationConnectionOperation.ConnectAsync("COM7", 115200,
            () => State(false), () => { request.Cancel(); return Task.CompletedTask; }, () => { },
            _ => { connected = true; return Task.CompletedTask; }, request.Token));
        Assert.False(connected);
    }

    [Fact]
    public async Task CancellationReachesPendingConnection()
    {
        using var request = new CancellationTokenSource();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken passedToken = default;
        var operation = AutomationConnectionOperation.ConnectAsync("COM7", 115200, () => State(false),
            () => Task.CompletedTask, () => { }, async token =>
            {
                passedToken = token;
                opening.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }, request.Token);
        await opening.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(passedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("COM8", 115200)]
    [InlineData("COM7", 9600)]
    public async Task ConnectionCompletedOnOtherTargetCannotReturnSuccess(string actualPort, int actualBaud)
    {
        var state = State(false);
        var error = await Assert.ThrowsAsync<AutomationException>(() => AutomationConnectionOperation.ConnectAsync("COM7", 115200,
            () => state, () => { state = State(true, actualPort, actualBaud); return Task.CompletedTask; }, () => { },
            _ => Task.CompletedTask, default));
        Assert.Equal("connection_mismatch", error.Code);
    }

    [Fact]
    public async Task SuccessfulConnectionReportsVerifiedActualTarget()
    {
        var state = State(false);
        var result = await AutomationConnectionOperation.ConnectAsync("com7", 115200, () => state,
            () => Task.CompletedTask, () => { }, _ => { state = State(true); return Task.CompletedTask; }, default);
        Assert.Equal("COM7", result.Port);
        Assert.Equal(115200, result.BaudRate);
    }

    [Fact]
    public void StartupCancellationCancelsConnectionLifetimeWhileScopeIsActive()
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using var startup = new ConnectionStartupCancellation(request.Token, lifetime);
        request.Cancel();
        Assert.True(lifetime.IsCancellationRequested);
    }

    [Fact]
    public void RequestEndingAfterStartupDoesNotCancelEstablishedConnection()
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using (new ConnectionStartupCancellation(request.Token, lifetime)) { }
        request.Cancel();
        Assert.False(lifetime.IsCancellationRequested);
    }
}
