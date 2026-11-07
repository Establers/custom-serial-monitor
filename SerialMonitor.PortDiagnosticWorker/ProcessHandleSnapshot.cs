using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialMonitor.PortDiagnosticWorker;

// ProcessHandleInformation (51), Windows x64: two pointer-sized header fields
// and 40-byte PROCESS_HANDLE_TABLE_ENTRY_INFO records. No global handle table.
internal sealed class ProcessHandleSnapshot : IDisposable
{
    internal const int MaximumBytes = 8 * 1024 * 1024;
    private const int HeaderBytes = 16, EntryBytes = 40;
    private const int LengthMismatch = unchecked((int)0xC0000004);
    internal delegate int Query(SafeProcessHandle process, nint buffer, int size, out int needed);
    private nint _buffer;
    public int Count { get; }
    private ProcessHandleSnapshot(nint buffer, int count) { _buffer = buffer; Count = count; }

    public (nint Handle, uint Type) Entry(int index)
    {
        ObjectDisposedException.ThrowIf(_buffer == 0, this);
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        var record = _buffer + HeaderBytes + index * EntryBytes;
        return (Marshal.ReadIntPtr(record), unchecked((uint)Marshal.ReadInt32(record, 28)));
    }

    public static ProcessHandleSnapshot? TryRead(SafeProcessHandle process, out bool limited, Query? query = null)
    {
        limited = false;
        query ??= NtQueryInformationProcess;
        var size = 64 * 1024;
        while (size <= MaximumBytes)
        {
            var buffer = Marshal.AllocHGlobal(size);
            var retained = false;
            try
            {
                var status = query(process, buffer, size, out var needed);
                if (status == 0)
                {
                    if (needed < HeaderBytes || needed > size) throw new InvalidDataException("Invalid process handle snapshot length.");
                    var count = Marshal.ReadInt64(buffer);
                    if (count < 0 || count > (needed - HeaderBytes) / EntryBytes)
                        throw new InvalidDataException("Invalid process handle snapshot count.");
                    retained = true;
                    return new ProcessHandleSnapshot(buffer, (int)count);
                }
                if (status != LengthMismatch) throw new InvalidOperationException($"Process handle snapshot unavailable: 0x{status:X8}.");
                // Reject an oversized process without allocating the requested
                // buffer. A leaking unrelated process cannot exhaust this worker.
                var nextSize = Math.Max((long)size * 2, (long)needed + 4096);
                if (nextSize > MaximumBytes) { limited = true; return null; }
                size = (int)nextSize;
            }
            finally { if (!retained) Marshal.FreeHGlobal(buffer); }
        }
        limited = true;
        return null;
    }

    public void Dispose() { if (_buffer != 0) { Marshal.FreeHGlobal(_buffer); _buffer = 0; } }

    [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
    private static extern int QueryProcessInformation(SafeProcessHandle process, int informationClass, nint information, int size, out int needed);
    private static int NtQueryInformationProcess(SafeProcessHandle process, nint information, int size, out int needed) =>
        QueryProcessInformation(process, 51, information, size, out needed);
}
