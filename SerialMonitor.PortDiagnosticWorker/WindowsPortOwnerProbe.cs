using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SerialMonitor.Core.Diagnostics;

namespace SerialMonitor.PortDiagnosticWorker;

internal static class WindowsPortOwnerProbe
{
    private const int MaximumProcesses = 1024;
    private const int MaximumExaminedHandles = 100_000;

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
        using var self = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
        using var snapshot = ProcessHandleSnapshot.TryRead(self, out _)
            ?? throw new InvalidOperationException("Diagnostic worker handle snapshot is unavailable.");
        uint fileType = 0;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot.Entry(i);
            if (entry.Handle == nul.DangerousGetHandle()) { fileType = entry.Type; break; }
        }
        if (fileType == 0) return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable, "Handle snapshot layout is unsupported.");
        var skipped = 0;
        var examined = 0;
        var limited = false;
        // Check the requesting app before enumerating other processes. Querying
        // one process at a time avoids allocating the system-wide handle table.
        if (Inspect(requester) is { } own) return Found(own);
        var pids = new int[MaximumProcesses];
        if (!K32EnumProcesses(pids, pids.Length * sizeof(int), out var bytes))
            return new(1, port, PortOwnerProbeStatus.Limited, [], skipped, "Process enumeration was unavailable.");
        if (bytes < 0 || bytes > pids.Length * sizeof(int) || bytes % sizeof(int) != 0)
            return PortOwnerProbeReport.Empty(port, PortOwnerProbeStatus.Unavailable);
        if (bytes == pids.Length * sizeof(int)) limited = true;
        var visited = 1;
        for (var i = 0; i < bytes / sizeof(int); i++)
        {
            var pid = pids[i];
            if (pid <= 0 || pid == requester || pid == Environment.ProcessId) continue;
            if (timer.ElapsedMilliseconds >= 1400 || examined >= MaximumExaminedHandles || visited >= MaximumProcesses) { limited = true; break; }
            visited++;
            if (Inspect(pid) is { } owner) return Found(owner);
        }
        return new(1, port, limited || skipped > 0 ? PortOwnerProbeStatus.Limited : PortOwnerProbeStatus.NotIdentified, [], skipped);

        PortOwnerProbeReport Found(PortOwnerProcess owner) => new(1, port, PortOwnerProbeStatus.Found, [owner], skipped);

        PortOwnerProcess? Inspect(int pid)
        {
            // ProcessHandleInformation requires QUERY_INFORMATION; no debug
            // privilege, termination, VM read/write or settings access is requested.
            using var process = OpenProcess(0x40 | 0x400, false, pid);
            if (process.IsInvalid) { skipped++; return null; }
            try
            {
                using var handles = ProcessHandleSnapshot.TryRead(process, out var oversized);
                if (handles is null) { limited |= oversized; skipped++; return null; }
                for (var i = 0; i < handles.Count; i++)
                {
                    if (timer.ElapsedMilliseconds >= 1400 || examined >= MaximumExaminedHandles) { limited = true; return null; }
                    var entry = handles.Entry(i);
                    if (entry.Type != fileType) continue;
                    examined++;
                    // SAME_ACCESS only; never CLOSE_SOURCE. Only copied handles
                    // are closed, without reading/writing or changing COM settings.
                    var duplicated = DuplicateHandle(process, entry.Handle, GetCurrentProcess(), out var copy, 0, false, 2);
                    using (copy)
                    {
                        if (!duplicated || GetFileType(copy) != 2) continue;
                        var name = ReadObjectName(copy);
                        if (!string.Equals(name, target, StringComparison.OrdinalIgnoreCase)) continue;
                        var executable = new StringBuilder(32768);
                        var length = executable.Capacity;
                        var processName = QueryFullProcessImageName(process, 0, executable, ref length)
                            ? Path.GetFileName(executable.ToString()) : "";
                        return new(pid, processName);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
            { limited = true; skipped++; }
            return null;
        }
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
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EnumProcesses([Out] int[] processIds, int bytes, out int needed);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(SafeFileHandle handle, int informationClass, nint information, int size, out int needed);
}
