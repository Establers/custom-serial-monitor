using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SerialMonitor.PortDiagnosticWorker;

namespace SerialMonitor.WinUI.Tests;

public sealed class ProcessHandleSnapshotTests
{
    private const int LengthMismatch = unchecked((int)0xC0000004);

    [Fact]
    public void OversizedProcess_IsLimitedWithoutAllocatingItsRequestedSnapshot()
    {
        using var process = new SafeProcessHandle(-1, ownsHandle: false);
        var calls = 0;
        var snapshot = ProcessHandleSnapshot.TryRead(process, out var limited, Query);
        Assert.Null(snapshot); Assert.True(limited); Assert.Equal(1, calls);
        int Query(SafeProcessHandle _, nint buffer, int size, out int needed)
        {
            calls++;
            Assert.Equal(64 * 1024, size);
            needed = 140_182_256; // Actual review reproduction: unrelated process has millions of handles.
            return LengthMismatch;
        }
    }

    [Fact]
    public void ValidSnapshot_UsesProcessEntryOffsetsAndBoundsItsLifetime()
    {
        using var process = new SafeProcessHandle(-1, ownsHandle: false);
        var calls = 0;
        using var snapshot = ProcessHandleSnapshot.TryRead(process, out var limited, Query)!;
        Assert.False(limited); Assert.Equal(2, calls); Assert.Equal(1, snapshot.Count);
        Assert.Equal(((nint)0x1234, (uint)0x3456), snapshot.Entry(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.Entry(1));
        snapshot.Dispose(); snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.Entry(0));
        int Query(SafeProcessHandle _, nint buffer, int size, out int needed)
        {
            calls++;
            if (calls == 1) { needed = 70_000; return LengthMismatch; }
            Assert.InRange(size, 70_000, ProcessHandleSnapshot.MaximumBytes);
            needed = 56;
            Marshal.WriteInt64(buffer, 1);
            Marshal.WriteIntPtr(buffer, 16, (nint)0x1234);
            Marshal.WriteInt32(buffer, 16 + 28, 0x3456);
            return 0;
        }
    }

    [Theory]
    [InlineData(-1, 56)]
    [InlineData(2, 56)]
    [InlineData(1, 15)]
    [InlineData(1, 70_000)]
    public void MalformedNativeReply_CannotReadOutsideItsReturnedBuffer(long count, int returned)
    {
        using var process = new SafeProcessHandle(-1, ownsHandle: false);
        Assert.Throws<InvalidDataException>(() => ProcessHandleSnapshot.TryRead(process, out _, Query));
        int Query(SafeProcessHandle _, nint buffer, int size, out int needed)
        { Marshal.WriteInt64(buffer, count); needed = returned; return 0; }
    }

    [Fact]
    public void RepeatedGrowth_RemainsBoundedEvenWhenNativeLengthIsStale()
    {
        using var process = new SafeProcessHandle(-1, ownsHandle: false);
        var calls = 0;
        Assert.Null(ProcessHandleSnapshot.TryRead(process, out var limited, Query));
        Assert.True(limited); Assert.InRange(calls, 1, 8);
        int Query(SafeProcessHandle _, nint buffer, int size, out int needed)
        {
            calls++; Assert.InRange(size, 1, ProcessHandleSnapshot.MaximumBytes);
            needed = 0; return LengthMismatch;
        }
    }
}
