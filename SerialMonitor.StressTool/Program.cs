using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using RJCP.IO.Ports;

namespace SerialMonitor.StressTool;

internal static class Program
{
    private const int FrameOverheadBytes = 22;

    public static int Main(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        StressOptions options;
        try
        {
            options = StressOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Argument error: {ex.Message}");
            Console.Error.WriteLine("Run with --help for usage.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            Run(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stress run failed: {ex.Message}");
            return 1;
        }
    }

    private static void Run(StressOptions options, CancellationToken cancellationToken)
    {
        PrintConfiguration(options);

        using var port = new SerialPortStream(options.Port, options.BaudRate, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteBufferSize = 128 * 1024,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 2_000
        };
        port.Open();
        port.DiscardOutBuffer();

        var random = new Random(options.Seed);
        var runStarted = Stopwatch.GetTimestamp();
        var reportStarted = runStarted;
        var previousWriteStarted = 0L;
        var packetCount = 0L;
        var byteCount = 0L;
        var reportPacketCount = 0L;
        var reportByteCount = 0L;
        var groupId = 0;
        var remainingInGroup = NextInclusive(random, options.MinPacketsPerGroup, options.MaxPacketsPerGroup);
        var previousGapWasGroupGap = false;
        var regularGaps = new GapStatistics();
        var groupGaps = new GapStatistics();

        while (!cancellationToken.IsCancellationRequested &&
               (options.DurationSeconds == 0 || Stopwatch.GetElapsedTime(runStarted).TotalSeconds < options.DurationSeconds))
        {
            var packetLength = NextInclusive(random, options.MinPacketBytes, options.MaxPacketBytes);
            var packet = CreatePacket(packetCount, groupId, packetLength, random);
            var writeStarted = Stopwatch.GetTimestamp();
            if (previousWriteStarted != 0)
            {
                var actualGap = Stopwatch.GetElapsedTime(previousWriteStarted, writeStarted);
                (previousGapWasGroupGap ? groupGaps : regularGaps).Observe(actualGap);
            }

            port.Write(packet, 0, packet.Length);
            previousWriteStarted = writeStarted;
            packetCount++;
            byteCount += packet.Length;

            var isGroupGap = false;
            double nextGapMilliseconds;
            if (options.Mode == StressMode.TimeoutProbe && --remainingInGroup == 0)
            {
                isGroupGap = true;
                groupId++;
                remainingInGroup = NextInclusive(random, options.MinPacketsPerGroup, options.MaxPacketsPerGroup);
                nextGapMilliseconds = NextDouble(random, options.MinGroupGapMs, options.MaxGroupGapMs);
            }
            else
            {
                nextGapMilliseconds = NextDouble(random, options.MinGapMs, options.MaxGapMs);
            }

            previousGapWasGroupGap = isGroupGap;
            if (!PreciseDelay(TimeSpan.FromMilliseconds(nextGapMilliseconds), cancellationToken))
            {
                break;
            }

            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(reportStarted, now) >= TimeSpan.FromSeconds(1))
            {
                var reportElapsed = Stopwatch.GetElapsedTime(reportStarted, now).TotalSeconds;
                var packetsThisReport = packetCount - reportPacketCount;
                var bytesThisReport = byteCount - reportByteCount;
                Console.WriteLine(
                    $"{Stopwatch.GetElapsedTime(runStarted, now):hh\\:mm\\:ss}  " +
                    $"packets={packetCount:N0}  bytes={byteCount:N0}  " +
                    $"rate={packetsThisReport / reportElapsed:N0} pkt/s, {bytesThisReport / reportElapsed / 1024:N1} KiB/s");
                reportStarted = now;
                reportPacketCount = packetCount;
                reportByteCount = byteCount;
            }
        }

        port.Flush();
        var elapsed = Stopwatch.GetElapsedTime(runStarted);
        Console.WriteLine();
        Console.WriteLine($"Completed: {packetCount:N0} packets, {byteCount:N0} bytes in {elapsed.TotalSeconds:N2}s");
        Console.WriteLine($"Short/start gaps: {regularGaps}");
        if (options.Mode == StressMode.TimeoutProbe)
        {
            Console.WriteLine($"Long/group gaps: {groupGaps}");
            Console.WriteLine($"Logical groups sent: {groupId:N0}");
        }
    }

    private static byte[] CreatePacket(long sequence, int groupId, int length, Random random)
    {
        var packet = new byte[length];
        packet[0] = (byte)'S';
        packet[1] = (byte)'M';
        packet[2] = (byte)'S';
        packet[3] = (byte)'T';
        BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(4, 8), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), checked((ushort)length));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(14, 4), groupId);
        random.NextBytes(packet.AsSpan(18, length - FrameOverheadBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(length - 4), ComputeCrc32(packet.AsSpan(0, length - 4)));
        return packet;
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }

        return ~crc;
    }

    private static bool PreciseDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)Math.Round(delay.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            if (remaining > TimeSpan.FromMilliseconds(2.5))
            {
                Thread.Sleep(Math.Max(1, (int)remaining.TotalMilliseconds - 2));
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    private static int NextInclusive(Random random, int minimum, int maximum) =>
        minimum == maximum ? minimum : random.Next(minimum, checked(maximum + 1));

    private static double NextDouble(Random random, double minimum, double maximum) =>
        minimum == maximum ? minimum : minimum + (random.NextDouble() * (maximum - minimum));

    private static void PrintConfiguration(StressOptions options)
    {
        var averagePacketBytes = (options.MinPacketBytes + options.MaxPacketBytes) / 2.0;
        var averageGapSeconds = (options.MinGapMs + options.MaxGapMs) / 2_000.0;
        var estimatedBytesPerSecond = averagePacketBytes / averageGapSeconds;
        var serialCapacityBytesPerSecond = options.BaudRate / 10.0;

        Console.WriteLine("Serial Monitor physical COM stress generator");
        Console.WriteLine($"Port={options.Port}, baud={options.BaudRate:N0}, mode={options.Mode}, seed={options.Seed}");
        Console.WriteLine(
            $"Packet={options.MinPacketBytes}..{options.MaxPacketBytes} bytes, " +
            $"short/start gap={options.MinGapMs:N2}..{options.MaxGapMs:N2} ms, " +
            $"duration={(options.DurationSeconds == 0 ? "until Ctrl+C" : $"{options.DurationSeconds:N0}s")}");
        if (options.Mode == StressMode.TimeoutProbe)
        {
            Console.WriteLine(
                $"Groups={options.MinPacketsPerGroup}..{options.MaxPacketsPerGroup} packets, " +
                $"long gap={options.MinGroupGapMs:N2}..{options.MaxGroupGapMs:N2} ms");
        }

        Console.WriteLine(
            $"Estimated offered load={estimatedBytesPerSecond / 1024:N1} KiB/s; " +
            $"8N1 line capacity={serialCapacityBytesPerSecond / 1024:N1} KiB/s");
        if (estimatedBytesPerSecond > serialCapacityBytesPerSecond * 0.9)
        {
            Console.WriteLine("WARNING: configured load is near or above the selected baud rate's 8N1 capacity.");
        }

        Console.WriteLine("Frame: 'SMST' + int64 sequence + uint16 length + int32 group + payload + CRC32.");
        Console.WriteLine("Press Ctrl+C to stop cleanly.");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            SerialMonitor.StressTool - deterministic binary traffic for a real/com0com COM path

            Usage:
              dotnet run --project SerialMonitor.StressTool -- [options]

            Common options:
              --port COM5                 Sender-side COM port (default: COM5)
              --baud 460800               Baud rate (default: 460800)
              --duration 60               Seconds; 0 means until Ctrl+C (default: 60)
              --mode load|timeout          Continuous load or timeout-boundary probe (default: load)
              --min-bytes 24               Minimum packet size including framing (default: 24)
              --max-bytes 96               Maximum packet size (default: 96)
              --min-gap-ms 3               Minimum packet-start gap after a write (default: 3)
              --max-gap-ms 5               Maximum packet-start gap after a write (default: 5)
              --seed 384009600             Reproducible random seed

            Timeout-probe options:
              --min-group-packets 2        Minimum packets before a long idle gap (default: 2)
              --max-group-packets 8        Maximum packets before a long idle gap (default: 8)
              --min-group-gap-ms 25        Minimum long idle gap (default: 25)
              --max-group-gap-ms 40        Maximum long idle gap (default: 40)

            Examples:
              dotnet run --project SerialMonitor.StressTool -- --port COM5 --duration 120
              dotnet run --project SerialMonitor.StressTool -- --port COM5 --mode timeout --duration 60
            """);
    }

    private sealed class GapStatistics
    {
        private long _count;
        private double _totalMilliseconds;
        private double _minimumMilliseconds = double.PositiveInfinity;
        private double _maximumMilliseconds;

        public void Observe(TimeSpan gap)
        {
            var milliseconds = gap.TotalMilliseconds;
            _count++;
            _totalMilliseconds += milliseconds;
            _minimumMilliseconds = Math.Min(_minimumMilliseconds, milliseconds);
            _maximumMilliseconds = Math.Max(_maximumMilliseconds, milliseconds);
        }

        public override string ToString() => _count == 0
            ? "none"
            : $"count={_count:N0}, min={_minimumMilliseconds:N3} ms, " +
              $"avg={_totalMilliseconds / _count:N3} ms, max={_maximumMilliseconds:N3} ms";
    }
}

internal enum StressMode
{
    Load,
    TimeoutProbe
}

internal sealed record StressOptions(
    string Port,
    int BaudRate,
    double DurationSeconds,
    StressMode Mode,
    int MinPacketBytes,
    int MaxPacketBytes,
    double MinGapMs,
    double MaxGapMs,
    int MinPacketsPerGroup,
    int MaxPacketsPerGroup,
    double MinGroupGapMs,
    double MaxGroupGapMs,
    int Seed)
{
    public static StressOptions Parse(string[] args)
    {
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "port", "baud", "duration", "mode", "min-bytes", "max-bytes",
            "min-gap-ms", "max-gap-ms", "min-group-packets", "max-group-packets",
            "min-group-gap-ms", "max-group-gap-ms", "seed"
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (!key.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            {
                throw new ArgumentException($"Expected --name value, received '{key}'.");
            }

            var normalizedKey = key[2..];
            if (!knownKeys.Contains(normalizedKey))
            {
                throw new ArgumentException($"Unknown option '--{normalizedKey}'.");
            }

            values[normalizedKey] = args[++index];
        }

        var modeText = Get(values, "mode", "load");
        var mode = modeText.ToLowerInvariant() switch
        {
            "load" => StressMode.Load,
            "timeout" or "timeout-probe" => StressMode.TimeoutProbe,
            _ => throw new ArgumentException("--mode must be 'load' or 'timeout'.")
        };

        var options = new StressOptions(
            Get(values, "port", "COM5"),
            ParseInt(values, "baud", 460_800),
            ParseDouble(values, "duration", 60),
            mode,
            ParseInt(values, "min-bytes", 24),
            ParseInt(values, "max-bytes", 96),
            ParseDouble(values, "min-gap-ms", 3),
            ParseDouble(values, "max-gap-ms", 5),
            ParseInt(values, "min-group-packets", 2),
            ParseInt(values, "max-group-packets", 8),
            ParseDouble(values, "min-group-gap-ms", 25),
            ParseDouble(values, "max-group-gap-ms", 40),
            ParseInt(values, "seed", 384_009_600));
        options.Validate();
        return options;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Port))
        {
            throw new ArgumentException("--port cannot be empty.");
        }

        RequireRange(BaudRate, 1, 10_000_000, "baud");
        RequireRange(DurationSeconds, 0, 86_400, "duration");
        RequireRange(MinPacketBytes, FrameMinimum, ushort.MaxValue, "min-bytes");
        RequireRange(MaxPacketBytes, MinPacketBytes, ushort.MaxValue, "max-bytes");
        RequireRange(MinGapMs, 0.1, 60_000, "min-gap-ms");
        RequireRange(MaxGapMs, MinGapMs, 60_000, "max-gap-ms");
        RequireRange(MinPacketsPerGroup, 1, 100_000, "min-group-packets");
        RequireRange(MaxPacketsPerGroup, MinPacketsPerGroup, 100_000, "max-group-packets");
        RequireRange(MinGroupGapMs, 0.1, 60_000, "min-group-gap-ms");
        RequireRange(MaxGroupGapMs, MinGroupGapMs, 60_000, "max-group-gap-ms");
        if (Mode == StressMode.TimeoutProbe && MinGroupGapMs <= MaxGapMs)
        {
            throw new ArgumentException("--min-group-gap-ms must be greater than --max-gap-ms in timeout mode.");
        }
    }

    private const int FrameMinimum = 22;

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        var text = Get(values, key, fallback.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"--{key} must be an integer.");
    }

    private static double ParseDouble(IReadOnlyDictionary<string, string> values, string key, double fallback)
    {
        var text = Get(values, key, fallback.ToString(CultureInfo.InvariantCulture));
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"--{key} must be a number.");
    }

    private static void RequireRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.");
        }
    }

    private static void RequireRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"--{name} must be between {minimum} and {maximum}.");
        }
    }
}
