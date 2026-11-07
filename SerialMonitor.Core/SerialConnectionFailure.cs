using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMonitor.Core.Diagnostics;

public enum SerialConnectFailureKind { AccessDenied, PortInUse, PortNotFound, DeviceUnavailable, TimedOut, InvalidSettings, OpenFailed, SessionStartupFailed, Unknown }
public sealed record SerialConnectionFailure(SerialConnectFailureKind Kind, int? NativeErrorCode, string ExceptionType, string TechnicalMessage)
{
    public bool NeedsOwnerLookup => Kind is SerialConnectFailureKind.AccessDenied or SerialConnectFailureKind.PortInUse or SerialConnectFailureKind.OpenFailed or SerialConnectFailureKind.Unknown;
}

public static class SerialConnectionFailureClassifier
{
    public static SerialConnectionFailure Classify(Exception exception)
    {
        var chain = new List<Exception>();
        for (var current = exception; current is not null && chain.Count < 16; current = current.InnerException)
            chain.Add(current);
        var leaf = chain[^1];
        var native = chain.OfType<Win32Exception>().LastOrDefault()?.NativeErrorCode;
        native ??= chain.Select(e => (uint)e.HResult).Where(h => (h & 0xFFFF0000) == 0x80070000).Select(h => (int?)(h & 0xFFFF)).LastOrDefault();
        var message = string.Join(" ← ", chain.AsEnumerable().Reverse().Select(e => e.Message[..Math.Min(512, e.Message.Length)]).Where(s => !string.IsNullOrWhiteSpace(s)));
        if (message.Length > 2048) message = message[..2048] + "…";
        var kind = native switch
        {
            5 => SerialConnectFailureKind.AccessDenied,
            32 or 33 => SerialConnectFailureKind.PortInUse,
            2 or 3 => SerialConnectFailureKind.PortNotFound,
            6 or 21 or 31 or 55 or 1167 => SerialConnectFailureKind.DeviceUnavailable,
            121 or 1460 => SerialConnectFailureKind.TimedOut,
            87 => SerialConnectFailureKind.InvalidSettings,
            _ => ClassifyManaged(chain, message)
        };
        return new(kind, native, leaf.GetType().Name, message);
    }

    private static SerialConnectFailureKind ClassifyManaged(List<Exception> chain, string message)
    {
        if (chain.Any(e => e is UnauthorizedAccessException)) return SerialConnectFailureKind.AccessDenied;
        if (chain.Any(e => e is FileNotFoundException or DirectoryNotFoundException)) return SerialConnectFailureKind.PortNotFound;
        if (chain.Any(e => e is TimeoutException)) return SerialConnectFailureKind.TimedOut;
        if (chain.Any(e => e is ArgumentException or NotSupportedException)) return SerialConnectFailureKind.InvalidSettings;
        // Native codes and types take precedence over localized message fallbacks.
        var text = message.ToLowerInvariant();
        if (text.Contains("access denied") || text.Contains("access is denied") || text.Contains("unauthorized") || text.Contains("액세스가 거부"))
            return SerialConnectFailureKind.AccessDenied;
        if (text.Contains("already in use") || text.Contains("being used") || text.Contains("busy") || text.Contains("사용 중"))
            return SerialConnectFailureKind.PortInUse;
        if (text.Contains("not found") || text.Contains("does not exist") || text.Contains("cannot find") || text.Contains("no such") || text.Contains("invalid port") || text.Contains("찾을 수 없"))
            return SerialConnectFailureKind.PortNotFound;
        if (text.Contains("semaphore timeout") || text.Contains("timed out")) return SerialConnectFailureKind.TimedOut;
        return chain.Any(e => e is IOException) ? SerialConnectFailureKind.OpenFailed : SerialConnectFailureKind.Unknown;
    }
}

public enum PortOwnerProbeStatus { Found, NotIdentified, PortNotPresent, Limited, TimedOut, Unavailable, NotNeeded }
public sealed record PortOwnerProcess(int ProcessId, string ProcessName);
public sealed record PortOwnerProbeReport(int Version, string PortName, PortOwnerProbeStatus Status,
    IReadOnlyList<PortOwnerProcess> Owners, int SkippedProcesses = 0, string? Detail = null)
{
    public static PortOwnerProbeReport Empty(string port, PortOwnerProbeStatus status, string? detail = null) => new(1, port, status, [], Detail: detail);
}
public sealed record SerialConnectFailureReport(string PortName, SerialConnectionFailure Failure, PortOwnerProbeReport OwnerProbe);

public static class PortOwnerProbeProtocol
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static string? NormalizePort(string? port)
    {
        if (port is null || port.Length > 32) return null;
        var name = port.StartsWith(@"\\.\", StringComparison.Ordinal) ? port[4..] : port;
        name = name.ToUpperInvariant();
        var offset = name.StartsWith("COM", StringComparison.Ordinal) ? 3 :
            name.StartsWith("CNCA", StringComparison.Ordinal) || name.StartsWith("CNCB", StringComparison.Ordinal) ? 4 : -1;
        if (offset < 0 || name.Length <= offset || name.Length - offset > 5 || name.AsSpan(offset).ContainsAnyExceptInRange('0', '9')) return null;
        return name;
    }
}
