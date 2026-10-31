using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SerialMonitor.Core.Tests;

internal sealed class TestProbeProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(64);
    private readonly Task _stdout;
    private readonly Task<string> _stderr;
    public TestProbeProcess(params string[] args)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SchedulingProbe", "SerialMonitor.TestProcessProbe.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("Build the test scheduling probe payload before running this test.", path);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(path);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        _process = Process.Start(start)!;
        _stdout = Task.Factory.StartNew(() =>
        {
            Exception? failure = null;
            try { while (_process.StandardOutput.ReadLine() is { } line) _lines.Writer.WriteAsync(line).AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; }
            finally { _lines.Writer.TryComplete(failure); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _stderr = Task.Factory.StartNew(_process.StandardError.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public async Task<JsonElement> ReadAsync(TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        try { return JsonSerializer.Deserialize<JsonElement>(await _lines.Reader.ReadAsync(deadline.Token)); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw new TimeoutException("Probe response deadline expired."); }
    }
    public void Send(string line) { _process.StandardInput.WriteLine(line); _process.StandardInput.Flush(); }
    public void SendBytes(byte[] bytes) { _process.StandardInput.BaseStream.Write(bytes); _process.StandardInput.BaseStream.Flush(); }
    public void InvokePreparedCli() { _process.StandardInput.BaseStream.WriteByte(1); _process.StandardInput.BaseStream.Flush(); }
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            await _stdout.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(await _stderr);
            Assert.Equal(0, _process.ExitCode);
        }
        finally { if (!_process.HasExited) _process.Kill(true); _process.Dispose(); }
    }
}
