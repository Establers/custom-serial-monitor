namespace SerialMonitor.WinUI.Services;

/// <summary>A response-window reservation bound to one transport connection.</summary>
public interface ISerialExclusiveSession : IAsyncDisposable
{
    long Generation { get; }

    Task SendAsync(byte[] payload, CancellationToken cancellationToken);

    // Fail closed after an unverified exchange without retiring a replacement
    // connection that happened to appear while cleanup waited for the driver.
    Task DisconnectAsync(CancellationToken cancellationToken);
}
