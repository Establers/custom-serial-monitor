using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using RJCP.IO.Ports;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.Tests;

public sealed class Com0ComNativeIdleStressTests
{
    [Fact]
    public async Task Com4Com5_RandomThreeToFiveMillisecondPackets_PreserveBytesAndIdleGroups()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SERIAL_COM0COM_STRESS_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        const int baudRate = 460_800;
        const int idleTimeoutMs = 15;
        const int groupCount = 80;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var receiver = new SerialService();
        await receiver.ConnectAsync(
            new SerialSettings
            {
                PortName = "COM4",
                BaudRate = baudRate,
                DataBits = 8,
                Parity = SerialParityMode.None,
                StopBits = SerialStopBitsMode.One,
                Handshake = SerialHandshakeMode.None,
                Encoding = RxEncodingMode.Hex
            },
            new SerialReceiveOptions
            {
                UseNativeIdleTimeout = true,
                IdleTimeoutMs = idleTimeoutMs
            },
            timeout.Token);

        using var sender = new SerialPortStream("COM5", baudRate, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteBufferSize = 128 * 1024,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 2_000
        };
        sender.Open();
        sender.DiscardOutBuffer();

        var expectedGroups = SendDeterministicGroups(sender, groupCount, timeout.Token);
        var actualGroups = new List<ReceivedByteChunk>(groupCount);
        while (actualGroups.Count < groupCount)
        {
            actualGroups.Add(await receiver.ReceivedBytes.ReadAsync(timeout.Token));
        }

        Assert.True(receiver.UsesNativeReceiveIdleTimeout);
        Assert.Equal(idleTimeoutMs, receiver.AppliedReceiveIdleTimeoutMs);
        Assert.Equal(groupCount, actualGroups.Count);
        Assert.All(actualGroups, chunk => Assert.True(chunk.EndsAtNativeIdleBoundary));
        for (var index = 0; index < expectedGroups.Count; index++)
        {
            Assert.Equal(expectedGroups[index], actualGroups[index].Bytes);
        }

        Assert.Equal(expectedGroups.Sum(group => (long)group.Length), receiver.ReceivedByteCount);
        Assert.Equal(groupCount, receiver.ReceivedChunkCount);
        Assert.Equal(0, receiver.ConnectionErrorCount);
        Assert.Equal(0, receiver.SerialFrameErrorCount);
        Assert.Equal(0, receiver.SerialParityErrorCount);
        Assert.Equal(0, receiver.SerialOverrunErrorCount);
        Assert.Equal(0, receiver.SerialRxOverErrorCount);
        await receiver.DisconnectAsync(timeout.Token);
    }

    [Fact]
    public async Task Com4Com5_LongRunningSoak_PreservesBoundedSequence()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SERIAL_COM0COM_SOAK_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var duration = ReadSoakDuration();
        const int baudRate = 460_800;
        const int idleTimeoutMs = 15;
        using var timeout = new CancellationTokenSource(duration + TimeSpan.FromMinutes(1));
        await using var receiver = new SerialService();
        await receiver.ConnectAsync(
            new SerialSettings
            {
                PortName = "COM4",
                BaudRate = baudRate,
                DataBits = 8,
                Parity = SerialParityMode.None,
                StopBits = SerialStopBitsMode.One,
                Handshake = SerialHandshakeMode.None,
                Encoding = RxEncodingMode.Hex
            },
            new SerialReceiveOptions
            {
                UseNativeIdleTimeout = true,
                IdleTimeoutMs = idleTimeoutMs
            },
            timeout.Token);

        using var sender = new SerialPortStream("COM5", baudRate, 8, Parity.None, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteBufferSize = 128 * 1024,
            ReadTimeout = Timeout.Infinite,
            WriteTimeout = 2_000
        };
        sender.Open();
        sender.DiscardOutBuffer();

        var expectedGroups = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var producer = Task.Run(
            () => ProduceSoakGroupsAsync(sender, expectedGroups.Writer, duration, timeout.Token),
            CancellationToken.None);
        var receivedGroups = 0L;
        var heartbeat = Stopwatch.StartNew();
        var pendingActual = Array.Empty<byte>();
        var pendingActualOffset = 0;

        try
        {
            while (await expectedGroups.Reader.WaitToReadAsync(timeout.Token))
            {
                while (expectedGroups.Reader.TryRead(out var expected))
                {
                    var expectedOffset = 0;
                    while (expectedOffset < expected.Length)
                    {
                        if (pendingActualOffset == pendingActual.Length)
                        {
                            var actual = await receiver.ReceivedBytes.ReadAsync(timeout.Token);
                            pendingActual = actual.Bytes;
                            pendingActualOffset = 0;
                        }

                        var expectedRemaining = expected.Length - expectedOffset;
                        var actualRemaining = pendingActual.Length - pendingActualOffset;
                        var compareLength = Math.Min(expectedRemaining, actualRemaining);
                        Assert.Equal(
                            expected.AsSpan(expectedOffset, compareLength).ToArray(),
                            pendingActual.AsSpan(pendingActualOffset, compareLength).ToArray());
                        expectedOffset += compareLength;
                        pendingActualOffset += compareLength;
                    }

                    receivedGroups++;

                    if (heartbeat.Elapsed >= TimeSpan.FromSeconds(30))
                    {
                        Console.WriteLine(
                            $"soak heartbeat groups={receivedGroups:N0} bytes={receiver.ReceivedByteCount:N0} " +
                            $"chunks={receiver.ReceivedChunkCount:N0} errors={receiver.ConnectionErrorCount:N0}");
                        heartbeat.Restart();
                    }
                }
            }

            await producer;
        }
        finally
        {
            timeout.Cancel();
            expectedGroups.Writer.TryComplete();
            try
            {
                await producer.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }

            await receiver.DisconnectAsync(CancellationToken.None);
        }

        Assert.True(receivedGroups > 0);
        Assert.Equal(0, receiver.ConnectionErrorCount);
        Assert.Equal(0, receiver.SerialFrameErrorCount);
        Assert.Equal(0, receiver.SerialParityErrorCount);
        Assert.Equal(0, receiver.SerialOverrunErrorCount);
        Assert.Equal(0, receiver.SerialRxOverErrorCount);
    }

    private static async Task ProduceSoakGroupsAsync(
        SerialPortStream sender,
        ChannelWriter<byte[]> expectedWriter,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var random = new Random(384_009_600);
        var sequence = 0;
        var group = 0;
        var deadline = DateTimeOffset.UtcNow + duration;
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                var expected = SendSoakGroup(sender, ref sequence, group++, random, cancellationToken);
                await expectedWriter.WriteAsync(expected, cancellationToken);
                PreciseDelay(TimeSpan.FromMilliseconds(32 + random.NextDouble() * 8), cancellationToken);
            }
        }
        finally
        {
            expectedWriter.TryComplete();
        }
    }

    private static byte[] SendSoakGroup(
        SerialPortStream sender,
        ref int sequence,
        int group,
        Random random,
        CancellationToken cancellationToken)
    {
        using var expectedGroup = new MemoryStream();
        var packetCount = random.Next(2, 9);
        for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packet = CreatePacket(random.Next(24, 97), sequence++, group, random);
            sender.Write(packet, 0, packet.Length);
            expectedGroup.Write(packet);

            if (packetIndex + 1 < packetCount)
            {
                PreciseDelay(TimeSpan.FromMilliseconds(3 + random.NextDouble() * 2), cancellationToken);
            }
        }

        return expectedGroup.ToArray();
    }

    private static TimeSpan ReadSoakDuration()
    {
        var value = Environment.GetEnvironmentVariable("SERIAL_COM0COM_SOAK_HOURS");
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) || hours <= 0)
        {
            throw new InvalidOperationException(
                "SERIAL_COM0COM_SOAK_HOURS must be a positive invariant-culture number.");
        }

        return TimeSpan.FromHours(hours);
    }

    private static IReadOnlyList<byte[]> SendDeterministicGroups(
        SerialPortStream sender,
        int groupCount,
        CancellationToken cancellationToken)
    {
        var random = new Random(384_009_600);
        var groups = new List<byte[]>(groupCount);
        var sequence = 0;

        for (var group = 0; group < groupCount; group++)
        {
            using var expectedGroup = new MemoryStream();
            var packetCount = random.Next(2, 9);
            for (var packetIndex = 0; packetIndex < packetCount; packetIndex++)
            {
                var packet = CreatePacket(random.Next(24, 97), sequence++, group, random);
                sender.Write(packet, 0, packet.Length);
                expectedGroup.Write(packet);

                if (packetIndex + 1 < packetCount)
                {
                    PreciseDelay(TimeSpan.FromMilliseconds(3 + (random.NextDouble() * 2)), cancellationToken);
                }
            }

            groups.Add(expectedGroup.ToArray());
            // More than twice the configured 15 ms timeout gives the scheduler
            // enough guard band while still exercising a dense packet stream.
            PreciseDelay(TimeSpan.FromMilliseconds(32 + (random.NextDouble() * 8)), cancellationToken);
        }

        sender.Flush();
        return groups;
    }

    private static byte[] CreatePacket(int length, int sequence, int group, Random random)
    {
        var packet = new byte[length];
        packet[0] = (byte)'S';
        packet[1] = (byte)'M';
        packet[2] = (byte)'S';
        packet[3] = (byte)'T';
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), group);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), length);
        random.NextBytes(packet.AsSpan(16));
        return packet;
    }

    private static void PreciseDelay(TimeSpan delay, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)Math.Round(delay.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return;
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
}
