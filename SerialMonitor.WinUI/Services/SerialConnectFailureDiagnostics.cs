using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialMonitor.Core.Diagnostics;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Services;

public interface IPortOwnerProbe
{
    Task<PortOwnerProbeReport> FindAsync(string portName, CancellationToken cancellationToken);
}

public interface ISerialConnectFailureDiagnostics
{
    Task<SerialConnectFailureReport> DiagnoseAsync(string portName, Exception exception, CancellationToken cancellationToken, bool openingPort = true);
}

public sealed class SerialConnectFailureDiagnostics(IPortOwnerProbe? ownerProbe = null) : ISerialConnectFailureDiagnostics
{
    private readonly IPortOwnerProbe _ownerProbe = ownerProbe ?? new WindowsPortOwnerProbeClient();
    public async Task<SerialConnectFailureReport> DiagnoseAsync(string portName, Exception exception, CancellationToken cancellationToken, bool openingPort = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failure = SerialConnectionFailureClassifier.Classify(exception);
        if (!openingPort) failure = failure with { Kind = SerialConnectFailureKind.SessionStartupFailed };
        var probe = PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.NotNeeded);
        if (failure.NeedsOwnerLookup)
        {
            try { probe = await _ownerProbe.FindAsync(portName, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                RuntimeDiagnostics.RecordError("ConnectFailure.OwnerProbe", ex);
                probe = PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable);
            }
        }
        return new(portName, failure, probe);
    }
}

public sealed class WindowsPortOwnerProbeClient : IPortOwnerProbe
{
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);
    private readonly string _workerPath;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo>? _startFactory;
    public WindowsPortOwnerProbeClient() : this(Path.Combine(AppContext.BaseDirectory, "PortDiagnostics", "SerialMonitor.PortDiagnosticWorker.exe"), TimeSpan.FromMilliseconds(2500)) { }
    internal WindowsPortOwnerProbeClient(string workerPath, TimeSpan timeout, Func<ProcessStartInfo>? startFactory = null)
    { _workerPath = workerPath; _timeout = timeout; _startFactory = startFactory; }

    public async Task<PortOwnerProbeReport> FindAsync(string portName, CancellationToken cancellationToken)
    {
        // Process startup and native file-system checks also stay off the UI
        // thread. A late startup retains the worker gate and cancels itself.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var work = Task.Run(() => FindCoreAsync(portName, deadline.Token), CancellationToken.None);
        try { return await work.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.TimedOut); }
        finally { deadline.Cancel(); _ = ObserveReadersAsync(work); }
    }

    private async Task<PortOwnerProbeReport> FindCoreAsync(string portName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = PortOwnerProbeProtocol.NormalizePort(portName);
        if (port is null || !OperatingSystem.IsWindows() || !File.Exists(_workerPath))
            return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable);
        if (!await ProbeGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable, "Another owner lookup is still running.");
        Process? process = null;
        var release = true;
        var started = false;
        try
        {
            process = new Process { StartInfo = _startFactory?.Invoke() ?? CreateStartInfo(_workerPath, port) };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);
            cancellationToken.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable);
            var output = ReadBoundedAsync(process.StandardOutput, 32768, deadline.Token);
            var errors = ReadBoundedAsync(process.StandardError, 2048, deadline.Token);
            // Observe both readers even if a timeout/limit aborts the worker.
            var reads = Task.WhenAll(output, errors);
            try
            {
                var completion = Task.WhenAll(process.WaitForExitAsync(deadline.Token), reads);
                try { await completion.WaitAsync(deadline.Token).ConfigureAwait(false); }
                finally { _ = ObserveReadersAsync(completion); }
                if (process.ExitCode == 3) return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.TimedOut);
                if (process.ExitCode != 0) return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable);
                return ParseReply(await output.ConfigureAwait(false), portName, port);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.TimedOut); }
            finally
            {
                deadline.Cancel();
                if (!process.HasExited) try { process.Kill(); } catch (Exception ex) { RuntimeDiagnostics.RecordError("PortOwnerProbe.Stop", ex); }
                try { await reads.WaitAsync(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false); }
                catch (Exception) { _ = ObserveReadersAsync(reads); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("PortOwnerProbe", ex);
            return PortOwnerProbeReport.Empty(portName, PortOwnerProbeStatus.Unavailable);
        }
        finally
        {
            if (started && process is not null && !process.HasExited)
            {
                // If driver teardown itself is delayed, retain just this worker
                // and its gate. Repeated Connect cannot accumulate helpers.
                release = false;
                _ = RetireAsync(process);
            }
            if (release) { process?.Dispose(); ProbeGate.Release(); }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string workerPath, string port)
    {
        var start = new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(workerPath))!
        };
        start.ArgumentList.Add(port);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        return start;
    }

    internal static PortOwnerProbeReport ParseReply(string json, string originalPort, string normalizedPort)
    {
        var result = JsonSerializer.Deserialize<PortOwnerProbeReport>(json, PortOwnerProbeProtocol.JsonOptions);
        if (result is null || result.Version != 1 || !Enum.IsDefined(result.Status) || !string.Equals(result.PortName, normalizedPort, StringComparison.Ordinal) ||
            result.Owners is null || result.Owners.Count > 8 || result.SkippedProcesses is < 0 or > 1024 || result.Detail?.Length > 1024 ||
            result.Owners.Any(o => o is null || o.ProcessId <= 0 || o.ProcessName is null || o.ProcessName.Length > 260 || o.ProcessName.Any(char.IsControl)) ||
            (result.Status == PortOwnerProbeStatus.Found) != (result.Owners.Count > 0))
            throw new InvalidDataException("Invalid port owner diagnostic reply.");
        return result with { PortName = originalPort };
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var buffer = new char[1024]; var text = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (text.Length + count > limit) throw new InvalidDataException("Port owner diagnostic output limit exceeded.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
    private static async Task ObserveReadersAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private static async Task RetireAsync(Process process)
    {
        var exited = false;
        try { await process.WaitForExitAsync().ConfigureAwait(false); exited = true; }
        catch (Exception ex) { RuntimeDiagnostics.RecordError("PortOwnerProbe.Retire", ex); }
        finally
        {
            // Do not launch another helper unless this one is known to have exited.
            if (exited) { process.Dispose(); ProbeGate.Release(); }
        }
    }
}
