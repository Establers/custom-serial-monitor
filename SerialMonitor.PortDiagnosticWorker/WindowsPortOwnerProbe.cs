using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SerialMonitor.Core.Diagnostics;

namespace SerialMonitor.PortDiagnosticWorker;

internal static class WindowsPortOwnerProbe
{
    private const int MaximumSnapshotBytes = 64 * 1024 * 1024;
    private const int MaximumProcesses = 1024;
    private const int MaximumExaminedHandles = 100_000;
    // Windows x64 SYSTEM_HANDLE_INFORMATION_EX: two pointer-sized header
    // fields followed by 40-byte SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX records.
    private const int HeaderBytes = 16, EntryBytes = 40;
    private const int LengthMismatch = unchecked((int)0xC0000004);

    public static PortOwnerProbeReport Find(string port, int requester)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable, "Windows x64 is required.");
        var timer = Stopwatch.StartNew();
        var targets = new char[32768];
        if (QueryDosDevice(port, targets, targets.Length) == 0)
            return PortOwnerProbeReport.Empty(port, Marshal.GetLastWin32Error() is 2 or 3
                ? PortOwnerProbeStatus.PortNotPresent : PortOwnerProbeStatus.Unavailable);
        var terminator = Array.IndexOf(targets, '\0');
        if (terminator <= 0) return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable);
        var target = new string(targets, 0, terminator);
        using var nul = CreateFile("NUL", 0, 3, 0, 3, 0, 0);
        if (nul.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var snapshot = Snapshot.Read();
        ushort fileType = 0;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot.Entry(i);
            if (entry.Pid == Environment.ProcessId && entry.Handle == nul.DangerousGetHandle()) { fileType = entry.Type; break; }
        }
        if (fileType == 0) return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable, "Handle snapshot layout is unsupported.");
        var processes = new Dictionary<int, SafeProcessHandle?>();
        var skipped = 0;
        var examined = 0;
        var limited = false;
        try
        {
            // Inspect this app first, including another window's stale handle.
            // Remaining entries require no sorting or unbounded per-handle list.
            for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < snapshot.Count; i++)
            {
                if (timer.ElapsedMilliseconds >= 1400 || examined >= MaximumExaminedHandles) { limited = true; goto Finished; }
                var entry = snapshot.Entry(i);
                if (entry.Type != fileType || entry.Pid <= 0 || entry.Pid == Environment.ProcessId || (pass == 0) != (entry.Pid == requester)) continue;
                if (!processes.TryGetValue(entry.Pid, out var process))
                {
                    if (processes.Count >= MaximumProcesses) { limited = true; goto Finished; }
                    process = OpenProcess(0x40 | 0x1000, false, entry.Pid); // DUP_HANDLE + QUERY_LIMITED_INFORMATION
                    if (process.IsInvalid) { process.Dispose(); process = null; skipped++; }
                    processes.Add(entry.Pid, process);
                }
                if (process is null) continue;
                examined++;
                // SAME_ACCESS only; never CLOSE_SOURCE and never read/write or
                // change timeouts/control lines on the duplicated device handle.
                if (!DuplicateHandle(process, entry.Handle, GetCurrentProcess(), out var copy, 0, false, 2)) continue;
                using (copy)
                {
                    // Skip disk and pipe handles before querying an object name.
                    if (GetFileType(copy) != 2) continue;
                    var name = ReadObjectName(copy);
                    if (!string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) continue;
                    var executable = new StringBuilder(32768);
                    var length = executable.Capacity;
                    var processName = QueryFullProcessImageName(process, 0, executable, ref length)
                        ? Path.GetFileName(executable.ToString()) : "";
                    return new(1, port, PortOwnerProbeStatus.Found, [new(entry.Pid, processName)], skipped);
                }
            }
        Finished:
            return new(1, port, limited || skipped > 0 ? PortOwnerProbeStatus.Limited : PortOwnerProbeStatus.NotIdentified, [], skipped);
        }
        finally { foreach (var process in processes.Values) process?.Dispose(); }
    }

    private static string? ReadObjectName(SafeFileHandle handle)
    {
        var size = 1024;
        while (size <= 65536)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryObject(handle, 1, buffer, size, out var needed);
                if (status == 0)
                {
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, 8);
                    var offset = text.ToInt64() - buffer.ToInt64();
                    if (length == 0) return null;
                    if (needed < 16 || needed > size || (length & 1) != 0 || offset < 16 || offset > needed - length) return null;
                    return Marshal.PtrToStringUni(text, length / 2);
                }
                if (needed <= size || needed > 65536) return null;
                size = needed;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private sealed class Snapshot : IDisposable
    {
        private nint _buffer;
        public int Count { get; }
        private Snapshot(nint buffer, int count) { _buffer = buffer; Count = count; }
        public (int Pid, nint Handle, ushort Type) Entry(int index)
        {
            var record = _buffer + HeaderBytes + index * EntryBytes;
            var pid = Marshal.ReadInt64(record, 8);
            return (pid is > 0 and <= int.MaxValue ? (int)pid : 0, Marshal.ReadIntPtr(record, 16), unchecked((ushort)Marshal.ReadInt16(record, 30)));
        }
        public static Snapshot Read()
        {
            var size = 1024 * 1024;
            while (size <= MaximumSnapshotBytes)
            {
                var buffer = Marshal.AllocHGlobal(size);
                var retained = false;
                try
                {
                    var status = NtQuerySystemInformation(64, buffer, size, out var needed);
                    if (status == 0)
                    {
                        var count = Marshal.ReadInt64(buffer);
                        if (needed < HeaderBytes || needed > size || count < 0 || count > (needed - HeaderBytes) / EntryBytes)
                            throw new InvalidDataException("Invalid handle snapshot length.");
                        retained = true;
                        return new Snapshot(buffer, (int)count);
                    }
                    if (status != LengthMismatch) throw new InvalidOperationException($"Handle snapshot unavailable: 0x{status:X8}.");
                    size = (int)Math.Min((long)MaximumSnapshotBytes + 1, Math.Max((long)size * 2, (long)needed + 65536));
                }
                finally { if (!retained) Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidDataException("Handle snapshot exceeds 64 MiB.");
        }
        public void Dispose() { if (_buffer != 0) { Marshal.FreeHGlobal(_buffer); _buffer = 0; } }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string name, [Out] char[] target, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, nint source, nint targetProcess, out SafeFileHandle target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, nint information, int size, out int needed);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(SafeFileHandle handle, int informationClass, nint information, int size, out int needed);
}
