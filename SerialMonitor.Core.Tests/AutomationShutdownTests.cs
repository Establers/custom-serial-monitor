using System.Collections.Concurrent;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Core.Tests;

public sealed class AutomationShutdownTests
{
    [Fact]
    public async Task DiscoveryWithdrawalFailureCanReportAndReenterStopWithoutStartingCleanupTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationShutdownTests), Guid.NewGuid().ToString("N"));
        var errors = new ConcurrentQueue<Exception>();
        Task? reentrant = null;
        var reports = 0;
        AutomationPipeHost? host = null;
        host = new("instance-" + Guid.NewGuid().ToString("N"), (_, _) => Task.FromResult(AutomationResponse.Ok(new { })), error =>
        {
            errors.Enqueue(error);
            // The GUI may respond to an automation error by starting shutdown.
            // Limit this callback to the first error so the regression cannot
            // overflow the stack if cleanup was not published before reporting.
            if (Interlocked.Increment(ref reports) == 1) reentrant = host!.StopAsync(default);
        }, root);
        try
        {
            host.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (AutomationDiscovery.List(root).Count == 0) await Task.Delay(5, deadline.Token);
            var descriptor = Path.Combine(root, host.Instance.InstanceId + ".json");
            File.Delete(descriptor);
            Directory.CreateDirectory(descriptor); // File.Delete must now fail.
            var stopping = host.StopAsync(default);
            await stopping.WaitAsync(deadline.Token);
            Assert.Same(stopping, reentrant);
            Assert.Same(stopping, host.StopAsync(default));
            Assert.Contains(errors, error => error is IOException or UnauthorizedAccessException);
            Assert.DoesNotContain(errors, error => error is ObjectDisposedException);
            await host.DisposeAsync();
        }
        finally
        {
            await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("sequence_cancel")]
    public async Task StopWithdrawsDiscoveryAndRejectsNewWorkBeforeBlockedCancellationCallbacksFinish(string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationShutdownTests), Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new TaskCompletionSource<AutomationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim(false);
        CancellationTokenRegistration registration = default;
        var calls = 0;
        var errors = new ConcurrentQueue<Exception>();
        var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, token) =>
        {
            Interlocked.Increment(ref calls);
            registration = token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(TimeSpan.FromSeconds(15)); });
            entered.TrySetResult();
            return work.Task;
        }, errors.Enqueue, root);
        host.Start();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (AutomationDiscovery.List(root).Count == 0) await Task.Delay(5, startup.Token);
        var client = AutomationPipeClient.CallAsync(host.Instance, operation, new { }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(AutomationDiscovery.List(root));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StopAsync(deadline.Token));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(AutomationDiscovery.List(root));
            Assert.False(host.StopAsync(default).IsCompleted);
            foreach (var lateOperation in new[] { "status", "sequence_cancel" })
            {
                using var lateDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await Record.ExceptionAsync(() => AutomationPipeClient.CallAsync(host.Instance, lateOperation, new { }, lateDeadline.Token));
            }
            Assert.Equal(1, calls);
            Assert.Empty(AutomationDiscovery.List(root));
        }
        finally
        {
            releaseCallback.Set();
            work.TrySetResult(AutomationResponse.Ok(new { }));
            await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await Record.ExceptionAsync(() => client.WaitAsync(TimeSpan.FromSeconds(3)));
            registration.Dispose();
            await host.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Assert.Empty(errors);
    }

    [Fact]
    public async Task StopRacingStartupCannotRepublishDiscoveryOrLeaveTemporaryDescriptors()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationShutdownTests), Guid.NewGuid().ToString("N"));
        var errors = new ConcurrentQueue<Exception>();
        try
        {
            for (var round = 0; round < 40; round++)
            {
                await using var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"),
                    (_, _) => Task.FromResult(AutomationResponse.Ok(new { })), errors.Enqueue, root);
                host.Start();
                if (round % 2 == 0) await Task.Yield();
                await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Empty(AutomationDiscovery.List(root));
                Assert.Empty(Directory.EnumerateFiles(root));
            }
            Assert.Empty(errors);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClientDisconnectInTheMiddleOfALargeReplyCannotFaultHostShutdown()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationShutdownTests), Guid.NewGuid().ToString("N"));
        var errors = new ConcurrentQueue<Exception>();
        var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"),
            (_, _) => Task.FromResult(AutomationResponse.Ok(new { commands = new string('X', 2 * 1024 * 1024) })), errors.Enqueue, root);
        try
        {
            host.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<IOException>(() =>
                AutomationPipeClient.CallAsync(host.Instance, "list_commands", new { }, timeout.Token));
            await host.StopAsync(timeout.Token);
            await host.DisposeAsync();
            Assert.Empty(errors);
        }
        finally
        {
            await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, "status")]
    [InlineData(true, "status")]
    [InlineData(false, "sequence_cancel")]
    [InlineData(true, "sequence_cancel")]
    public async Task StopDeadlineBoundsANonCooperativeHandlerAndAllowsSafeLateCleanup(bool lateFailure, string operation)
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(AutomationShutdownTests), Guid.NewGuid().ToString("N"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AutomationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        CancellationToken requestToken = default;
        var host = new AutomationPipeHost("instance-" + Guid.NewGuid().ToString("N"), (_, token) =>
        {
            requestToken = token;
            entered.TrySetResult();
            return release.Task; // Deliberately ignore cancellation.
        }, errors.Enqueue, root);
        host.Start();
        var client = AutomationPipeClient.CallAsync(host.Instance, operation, new { }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StopAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(requestToken.IsCancellationRequested);
            Assert.Empty(AutomationDiscovery.List(root));
            var cleanup = host.StopAsync(default);
            Assert.False(cleanup.IsCompleted);
            if (lateFailure) release.SetException(new InvalidOperationException("Late handler failure"));
            else release.SetResult(new(true, System.Text.Json.JsonSerializer.SerializeToElement(new { })));
            await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync(); // Repeated stop/disposal must share completed cleanup.
            await Record.ExceptionAsync(() => client.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.DoesNotContain(errors, error => error is ObjectDisposedException);
            if (lateFailure) Assert.Contains(errors, error => error.Message == "Late handler failure");
            else Assert.Empty(errors);
        }
        finally
        {
            release.TrySetCanceled();
            await host.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            await Record.ExceptionAsync(() => client);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
