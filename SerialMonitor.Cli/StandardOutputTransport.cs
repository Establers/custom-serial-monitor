using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialMonitor.Cli;

internal static class StandardOutputTransport
{
    public static Stream Open()
    {
        if (!OperatingSystem.IsWindows() || !Console.IsOutputRedirected) return Console.OpenStandardOutput();
        // Windows' Console stream treats a broken output pipe as a successful
        // write. Automation must observe that failure rather than exit 0 with
        // an empty/lost reply. FileStream preserves the pipe's I/O errors.
        // The process owns stdout; disposing this wrapper must not close it.
        var handle = new SafeFileHandle(GetStdHandle(-11), ownsHandle: false);
        return new FileStream(handle, FileAccess.Write, bufferSize: 1, isAsync: false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int standardHandle);
}
