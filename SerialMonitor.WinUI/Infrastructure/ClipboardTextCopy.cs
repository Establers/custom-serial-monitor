using Windows.ApplicationModel.DataTransfer;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class ClipboardTextCopy
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private const int MaximumAttempts = 5;

    public static Task CopyAsync(string text, CancellationToken cancellationToken) =>
        CopyAsync(text, SetText, Clipboard.Flush, Task.Delay, cancellationToken);

    internal static async Task CopyAsync(string text, Action<string> setText, Action flush,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        // Keep the caller's UI context: WinRT clipboard calls must stay on that thread.
        // Retry Flush separately so clipboard listeners do not receive repeated SetContent events.
        await RetryAsync(() => setText(text), delay, cancellationToken);
        await RetryAsync(flush, delay, cancellationToken);
    }

    private static void SetText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private static async Task RetryAsync(Action action, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex.HResult == ClipboardCannotOpen && attempt < MaximumAttempts)
            {
                await delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken);
            }
        }
    }
}
