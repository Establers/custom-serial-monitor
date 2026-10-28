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
using System.Text;
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
            try { await source.CopyToAsync(destination).ConfigureAwait(false); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { destination.Dispose(); }
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
                    var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                    var error = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                    process.WaitForExit();
                    Task.WaitAll(output, error);
                    return process.ExitCode;
                }
                finally {
                    if (!process.HasExited) { try { process.Kill(); } catch (InvalidOperationException) { } }
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
    [Console]::Error.WriteLine('Serial Monitor plugin: ' + $_.Exception.Message)
    exit 1
}
finally {
    if ($child) {
        if (-not $child.HasExited) { try { $child.Kill() } catch {} }
        $child.Dispose()
    }
}
