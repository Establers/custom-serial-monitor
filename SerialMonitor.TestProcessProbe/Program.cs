using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using SerialMonitor.Cli;
using SerialMonitor.Core.Automation;
using SerialMonitor.Core.Tests;

// Test-only process isolation. Never change the testhost's global worker limits,
// and never add hidden runtime switches to the shipping CLI to exercise starvation.
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (args[0] == "hold-stdio") { Console.OpenStandardInput().ReadByte(); return 0; }
            if (args[0] == "prepared-cli")
            {
                var startup = Stopwatch.StartNew();
                Thread.Sleep(int.Parse(args[1])); // Deliberate test-only startup delay.
                _ = typeof(CliCommandInput).Assembly.EntryPoint;
                Console.WriteLine(JsonSerializer.Serialize(new { ready = true, startupMs = startup.Elapsed.TotalMilliseconds }));
                if (Console.OpenStandardInput().ReadByte() != 1) throw new IOException("CLI invocation gate closed.");
                return InvokeCli(args[2..]);
            }

            ThreadPool.GetMinThreads(out _, out var minimumIo);
            ThreadPool.GetMaxThreads(out _, out var maximumIo);
            if (!ThreadPool.SetMinThreads(1, minimumIo) || !ThreadPool.SetMaxThreads(1, maximumIo))
                throw new InvalidOperationException("Could not establish the isolated one-worker test condition.");
            if (args[0] == "mcp") return InvokeCli(args);
            if (args[0] == "process-reader")
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("hold-stdio");
                using var child = Process.Start(start)!;
                try
                {
                    var pending = args[1] == "raw" ? child.StandardError.ReadToEndAsync() : ProcessStreamReader.ReadToEndAsync(child.StandardError);
                    var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ThreadPool.QueueUserWorkItem(_ => tick.TrySetResult());
                    var ran = tick.Task.Wait(TimeSpan.FromMilliseconds(400));
                    Console.WriteLine(JsonSerializer.Serialize(new { heartbeatRan = ran,
                        asyncHandle = child.StandardError.BaseStream is FileStream file && file.IsAsync }));
                    child.StandardInput.Close();
                    if (!child.WaitForExit(5000)) throw new TimeoutException("Owned stdio fixture did not exit.");
                    _ = pending.GetAwaiter().GetResult();
                    return 0;
                }
                finally { if (!child.HasExited) child.Kill(true); }
            }

            using var input = OpenInput(args[1] == "raw");
            var pendingInput = input.ReadAsync(new char[1].AsMemory()).AsTask();
            var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // On the old ConsoleStream adapter, this gets queued behind the
            // synchronous anonymous-pipe read until another stdin byte arrives.
            ThreadPool.QueueUserWorkItem(_ => heartbeat.TrySetResult());
            var heartbeatRan = heartbeat.Task.Wait(TimeSpan.FromMilliseconds(400));
            if (args[0] == "stdin")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { heartbeatRan, inputCompleted = pendingInput.IsCompleted,
                    readerType = input.GetType().FullName, workers = ThreadPool.ThreadCount }));
                return 0;
            }

            var instance = JsonSerializer.Deserialize<AutomationInstance>(args[2], AutomationJson.Options)!;
            // Force the first production connection probe to fail before the
            // parent starts its real host. Merely emitting a pre-call marker
            // races process scheduling and can let the first probe succeed.
            using var occupiedServer = new NamedPipeServerStream(instance.PipeName, PipeDirection.InOut, 1);
            using var occupant = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut);
            occupant.Connect(1000);
            occupiedServer.WaitForConnection();
            var watch = Stopwatch.StartNew();
            using var callStop = new CancellationTokenSource();
            var call = AutomationPipeClient.CallAsync(instance, "send", new { command = "probe-only" }, callStop.Token);
            occupant.Dispose();
            occupiedServer.Dispose();
            Console.WriteLine("{\"stage\":\"connecting\"}");
            var completed = call.Wait(TimeSpan.FromSeconds(2));
            // Releasing the raw stdin fixture during parent cleanup must not
            // permit the deliberately stalled connection to send a late request.
            if (!completed) callStop.Cancel();
            Console.WriteLine(JsonSerializer.Serialize(new { completed, success = completed && call.GetAwaiter().GetResult().Success,
                heartbeatRan, elapsedMs = watch.Elapsed.TotalMilliseconds, readerType = input.GetType().FullName }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static TextReader OpenInput(bool raw)
    {
        if (!raw)
        {
            var factory = typeof(CliCommandInput).Assembly.GetType("SerialMonitor.Cli.StandardInputTransport");
            if (factory is not null)
                return (TextReader)factory.GetMethod("Open", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        }
        return new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false, 4096, true);
    }

    private static int InvokeCli(string[] args)
    {
        var result = typeof(CliCommandInput).Assembly.EntryPoint!.Invoke(null, [args]);
        return result switch { int code => code, Task<int> task => task.GetAwaiter().GetResult(), _ => throw new InvalidOperationException("Unexpected CLI entry point.") };
    }
}
