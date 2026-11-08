[CmdletBinding()]
param(
    [string]$CliPath,
    [ValidateSet('mcp', 'describe')][string]$Mode = 'mcp'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$child = $null
try {
    $pluginRoot = Split-Path -Parent $PSScriptRoot
    $compatibility = [IO.File]::ReadAllText((Join-Path $pluginRoot 'compatibility.json')) | ConvertFrom-Json
    $executable = & (Join-Path $PSScriptRoot 'Resolve-SerialMonitorCli.ps1') -CliPath $CliPath

    # Probe without a GUI, COM access or device operations. Bound both streams and
    # the child lifetime; a failed/old CLI must not leak a process or deadlock MCP.
    if (-not ('SerialMonitor.Plugin.CliProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace SerialMonitor.Plugin {
    public static class CliProbe {
        private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum) {
            var text = new StringBuilder();
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) {
                if (text.Length + count > maximum) throw new InvalidDataException("CLI metadata exceeds the allowed size.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        public static string Read(string executable, int timeoutMs) {
            var start = new ProcessStartInfo(executable, "describe --json") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            using (var process = Process.Start(start)) {
                if (process == null) throw new InvalidOperationException("Could not start serialctl.");
                process.StandardInput.Close();
                var output = ReadBoundedAsync(process.StandardOutput, 1048576);
                var error = ReadBoundedAsync(process.StandardError, 16384);
                if (!process.WaitForExit(timeoutMs)) {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    process.WaitForExit(1000);
                    throw new TimeoutException("serialctl describe timed out. Update or repair the application.");
                }
                Task.WaitAll(new Task[] { output, error }, 1000);
                if (!output.IsCompleted || !error.IsCompleted) throw new TimeoutException("CLI metadata streams did not close.");
                if (process.ExitCode != 0) throw new InvalidOperationException("The installed CLI does not support plugin discovery. Update Serial Monitor. " + error.Result);
                return output.Result;
            }
        }
        private static async Task ForwardInputAsync(Stream source, Stream destination) {
            try {
                var buffer = new byte[8192];
                int count;
                while ((count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) {
                    await destination.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                    // The child's stdin FileStream buffers small writes. MCP clients
                    // keep stdin open while waiting for each response, so flush each
                    // chunk rather than waiting for the buffer to fill or reach EOF.
                    await destination.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { destination.Dispose(); }
        }
        private static void Observe(Task task) {
            task.ContinueWith(t => { var error = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int standardHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, int count, out int written, IntPtr overlapped);
        private static void WriteOutput(bool error, byte[] buffer, int count) {
            if (error ? !Console.IsErrorRedirected : !Console.IsOutputRedirected) {
                (error ? Console.OpenStandardError() : Console.OpenStandardOutput()).Write(buffer, 0, count);
                return;
            }
            // .NET Framework console/file streams can silently accept broken
            // Windows pipes. Preserve the native failure instead.
            int written;
            if (!WriteFile(GetStdHandle(error ? -12 : -11), buffer, count, out written, IntPtr.Zero))
                throw new IOException("MCP output forwarding failed.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            if (written != count) throw new IOException("MCP output forwarding was incomplete.");
        }
        public static void ReportFailure(string message) {
            var write = Task.Run(() => Console.Error.WriteLine(message));
            Observe(write);
            // stderr itself can be the broken or blocked stream.
            Task.WhenAny(write, Task.Delay(1000)).GetAwaiter().GetResult();
        }
        private static async Task ForwardOutputAsync(Stream source, bool error) {
            var buffer = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) {
                var length = count;
                // Native writes to synchronous host pipes can block. Keep the
                // deadline watcher independent of that worker.
                var write = Task.Run(() => WriteOutput(error, buffer, length));
                using (var deadline = new CancellationTokenSource()) {
                    var timeout = Task.Delay(5000, deadline.Token);
                    if (await Task.WhenAny(write, timeout).ConfigureAwait(false) != write) {
                        Observe(write);
                        throw new TimeoutException("MCP output forwarding timed out. Inspect capture state before retrying; TX is never replayed.");
                    }
                    deadline.Cancel();
                    await write.ConfigureAwait(false);
                }
            }
        }
        public static int RunMcp(string executable) {
            var start = new ProcessStartInfo(executable, "mcp") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = Process.Start(start)) {
                if (process == null) throw new InvalidOperationException("Could not start the Serial Monitor MCP process.");
                try {
                    // Copy bytes rather than PowerShell objects or decoded lines.
                    // Closing the input stream propagates client EOF to the CLI.
                    var input = ForwardInputAsync(Console.OpenStandardInput(), process.StandardInput.BaseStream);
                    var output = ForwardOutputAsync(process.StandardOutput.BaseStream, false);
                    var error = ForwardOutputAsync(process.StandardError.BaseStream, true);
                    var exited = Task.Run(() => process.WaitForExit());
                    var forwarding = Task.WhenAll(output, error);
                    Observe(input);
                    Observe(forwarding);
                    Observe(exited);
                    // Watch forwarding failures while stdin remains open. Waiting
                    // only for child exit hides a broken host pipe from the CLI.
                    var finished = Task.WhenAny(input, output, error, exited).GetAwaiter().GetResult();
                    finished.GetAwaiter().GetResult();
                    if (finished == output || finished == error) process.StandardInput.Close();
                    if (!exited.Wait(5000)) throw new TimeoutException("MCP did not stop after the transport closed.");
                    if (Task.WhenAny(forwarding, Task.Delay(5000)).GetAwaiter().GetResult() != forwarding)
                        throw new TimeoutException("MCP output streams did not close.");
                    forwarding.GetAwaiter().GetResult();
                    return process.ExitCode;
                }
                finally {
                    if (!process.HasExited) { try { process.Kill(); } catch (InvalidOperationException) { } }
                    process.WaitForExit(1000);
                }
            }
        }
    }
}
'@
    }
    $description = [SerialMonitor.Plugin.CliProbe]::Read($executable, [int]$compatibility.probeTimeoutMs)
    $response = $description | ConvertFrom-Json
    $data = $response.data
    if (-not $response.success -or -not $data -or $data.schemaVersion -ne 1) {
        throw 'The installed CLI does not provide compatible plugin metadata. Update Serial Monitor.'
    }
    $apiVersion = [version]$data.controlApiVersion
    $cliVersion = [version](([string]$data.cliVersion -split '[-+]')[0])
    if ($apiVersion.Major -ne [int]$compatibility.controlApiMajor) {
        throw 'The program control API is incompatible with this plugin. Update the Serial Monitor plugin and program to compatible versions.'
    }
    if ($cliVersion -lt [version]$compatibility.minimumCliVersion) {
        throw "Update Serial Monitor to $($compatibility.minimumCliVersion) or later before using this plugin."
    }
    if (-not $data.minimumPluginVersion -or [version]$data.minimumPluginVersion -gt [version]$compatibility.pluginVersion) {
        throw 'The program requires a newer Serial Monitor plugin. Update the plugin before reconnecting.'
    }
    if ($Mode -eq 'describe') {
        [Console]::Out.Write($description)
        exit 0
    }

    # No shell, concatenated command, PowerShell text pipeline or startup text.
    # Explicit raw byte forwarding also works when the host has no console.
    exit [SerialMonitor.Plugin.CliProbe]::RunMcp($executable)
}
catch {
    $message = 'Serial Monitor plugin: ' + $_.Exception.Message
    if ('SerialMonitor.Plugin.CliProbe' -as [type]) { [SerialMonitor.Plugin.CliProbe]::ReportFailure($message) }
    else { [Console]::Error.WriteLine($message) }
    exit 1
}
finally {
    if ($child) {
        if (-not $child.HasExited) { try { $child.Kill() } catch {} }
        $child.Dispose()
    }
}
