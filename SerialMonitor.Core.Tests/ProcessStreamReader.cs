namespace SerialMonitor.Core.Tests;

internal static class ProcessStreamReader
{
    // Redirected Windows process pipes are synchronous streams. Their async
    // reads can occupy a pool worker until child exit, starving the simulated
    // GUI hosted on that same pool. Keep each bounded child stream off the pool.
    public static Task<string> ReadToEndAsync(StreamReader reader) => Task.Factory.StartNew(
        reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
