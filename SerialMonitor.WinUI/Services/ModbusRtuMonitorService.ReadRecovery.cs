using System.Diagnostics;

namespace SerialMonitor.WinUI.Services;

public sealed partial class ModbusRtuMonitorService
{
    // Respect longer user intervals. Failures never create a catch-up queue.
    internal static int ReadRetryDelay(int intervalMs, int failures) =>
        Math.Max(intervalMs, Math.Min(30000, 1000 << Math.Clamp(failures - 1, 0, 5)));

    private async Task RecoverReadAsync(ISerialExclusiveSession lease, ModbusReceiveInbox inbox,
        int timeoutMs, ModbusExchangeException error, CancellationToken stop)
    {
        if (!error.UnknownCompletion) return;
        try
        {
            // Keep exclusive ownership and discard late/partial RX for another response
            // timeout, then require an idle gap before any next read OR queued mutation.
            // RTU has no transaction ID: this is a bounded turnaround guard, not a proof
            // against a device replying arbitrarily late to an identical request.
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs)
            {
                stop.ThrowIfCancellationRequested();
                inbox.DiscardPending();
                await Task.Delay(5, stop).ConfigureAwait(false);
            }
            var gapMs = Math.Max(5, (int)Math.Ceiling(3.5 * 12 * 1000 / Math.Max(1, _serial.AppliedBaudRate)));
            await inbox.WaitForQuietAsync(gapMs, Math.Max(timeoutMs, gapMs * 2), stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // A stop must not hand an unresolved response to manual TX or another owner.
            await lease.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
