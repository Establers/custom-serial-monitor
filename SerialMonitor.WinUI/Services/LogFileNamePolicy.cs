namespace SerialMonitor.WinUI.Services;

public static class LogFileNamePolicy
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public static bool TryValidate(string? value, out string fileName, out string error)
    {
        fileName = value ?? string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = string.Empty;
            return true;
        }

        if (fileName.Length > 255)
        {
            error = "Log file name must be 255 characters or fewer.";
            return false;
        }

        if (fileName is "." or ".." ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.'))
        {
            error = "Log file name cannot end with a space or period.";
            return false;
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Any(char.IsControl) ||
            fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            error = "Log file name must be a file name only and cannot contain path or invalid characters.";
            return false;
        }

        var reservedNameCandidate = fileName.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(reservedNameCandidate))
        {
            error = $"'{fileName}' is a reserved Windows file name.";
            return false;
        }

        return true;
    }

    public static string Validate(string? value)
    {
        if (TryValidate(value, out var fileName, out var error))
        {
            return fileName;
        }

        throw new ArgumentException(error, nameof(value));
    }
}
