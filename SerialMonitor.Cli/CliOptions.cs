namespace SerialMonitor.Cli;

public static class CliOptions
{
    public static Dictionary<string, string> Parse(string command, string[] flags)
    {
        var fields = command switch
        {
            "help" or "--help" or "-h" or "describe" or "instances" => "",
            "mcp" => "instance tool-content",
            "status" or "ports" or "disconnect" or "cancel" => "instance",
            "commands" => "instance offset limit",
            "connect" => "instance port baud",
            "configure" => "instance port baud byte-timeout-ms",
            "mode" => "instance set",
            "send" => "instance port text text-file hex ending preview-bytes format",
            "request" or "send-and-capture" => "instance port text text-file hex ending preview-bytes format duration-ms",
            "capture" => "instance port duration-ms preview-bytes format",
            "read" => "instance capture offset limit format",
            _ => throw new ArgumentException("Unknown command: " + command)
        };
        var allowed = fields.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i] == "--json") continue;
            var option = flags[i];
            var equal = option.IndexOf('=');
            var name = equal >= 0 ? option[..equal] : option;
            if (!name.StartsWith("--") || !allowed.Contains(name[2..]))
                throw new ArgumentException("Unsupported option for " + command + ": " + name);
            string value;
            if (equal >= 0) value = option[(equal + 1)..];
            else
            {
                if (i + 1 >= flags.Length || flags[i + 1].StartsWith("--"))
                    throw new ArgumentException("Missing value for " + name + ". Use " + name + "=VALUE for a value beginning with --.");
                value = flags[++i];
            }
            if (!result.TryAdd(name[2..], value)) throw new ArgumentException("Duplicate option: " + name);
        }
        return result;
    }
}
