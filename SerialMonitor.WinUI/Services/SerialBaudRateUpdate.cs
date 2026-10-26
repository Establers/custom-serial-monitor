namespace SerialMonitor.WinUI.Services;

// RJCP stages its baud value before calling SetCommState. Restore both the
// driver and the staged value on failure; its property getter alone is not a
// driver acknowledgement, so readback must fetch GetCommState first.
internal static class SerialBaudRateUpdate
{
    internal static int Apply(int requestedBaudRate, Func<int> readDriverBaudRate, Action<int> writeBaudRate)
    {
        var previousBaudRate = readDriverBaudRate();
        if (previousBaudRate == requestedBaudRate) return previousBaudRate;
        try
        {
            writeBaudRate(requestedBaudRate);
            var appliedBaudRate = readDriverBaudRate();
            if (appliedBaudRate != requestedBaudRate)
                throw new IOException($"The serial driver reported {appliedBaudRate} bps instead of {requestedBaudRate} bps.");
            return appliedBaudRate;
        }
        catch (Exception updateError)
        {
            try
            {
                writeBaudRate(previousBaudRate);
                if (readDriverBaudRate() != previousBaudRate)
                    throw new IOException("The serial driver did not restore the previous baud rate.");
            }
            catch (Exception restoreError)
            {
                throw new SerialBaudRateUpdateException(
                    "Baud rate update failed and the previous driver setting could not be verified. Inspect connection status before further TX.",
                    stateRestored: false, new AggregateException(updateError, restoreError));
            }
            throw new SerialBaudRateUpdateException(
                "Baud rate update failed; the previous driver setting was restored: " + updateError.Message,
                stateRestored: true, updateError);
        }
    }
}

internal sealed class SerialBaudRateUpdateException(string message, bool stateRestored, Exception innerException)
    : IOException(message, innerException)
{
    internal bool StateRestored { get; } = stateRestored;
}
