namespace SerialMonitor.WinUI.Services;

public sealed class ProfileConflictException(string message) : IOException(message);
