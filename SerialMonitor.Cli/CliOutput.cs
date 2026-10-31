using System.Text;

namespace SerialMonitor.Cli;

public sealed class CliOutputException(string message, Exception inner) : IOException(message, inner);

internal static class CliOutput
{
    public static async Task WriteLineAsync(string value, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        // Console.Out can synchronously block inside WriteLine on a full pipe.
        // Do not dispose/flush this writer after an interrupted output: a partial
        // JSON result must never be followed by another JSON error or retried TX.
        var write = Task.Run(async () =>
        {
            var writer = new StreamWriter(StandardOutputTransport.Open(), new UTF8Encoding(false), 4096, true);
            await writer.WriteLineAsync(value.AsMemory(), deadline.Token).ConfigureAwait(false);
            await writer.FlushAsync(deadline.Token).ConfigureAwait(false);
        });
        try { await write.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _ = write.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new CliOutputException($"CLI output failed or exceeded its 5-second deadline. {ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message} Inspect GUI/capture state before retrying; TX is never replayed.", ex);
        }
    }

    public static void ReportFailure(CliOutputException error)
    {
        try { Console.Error.WriteLine(error.Message); }
        catch (IOException) { } // The consumer may also have closed stderr.
    }
}
