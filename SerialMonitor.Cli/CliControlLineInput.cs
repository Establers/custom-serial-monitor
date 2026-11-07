using System.Text.Json;
using SerialMonitor.Core.Automation;

namespace SerialMonitor.Cli;

public static class CliControlLineInput
{
    public static AutomationRequest Parse(IReadOnlyDictionary<string, string> flags)
    {
        var changes = new[] { "dtr", "rts", "rts-toggle" }.Where(flags.ContainsKey).ToArray();
        if (changes.Length > 1) throw new ArgumentException("Change one control line per command to receive its verified result.");
        var arguments = new Dictionary<string, object>();
        if (flags.TryGetValue("instance", out var instance)) arguments["instanceId"] = instance;
        if (flags.TryGetValue("port", out var port)) arguments["port"] = port;
        if (changes.Length == 0)
        {
            if (flags.ContainsKey("generation")) throw new ArgumentException("--generation applies only to a control-line change.");
            return new("get_control_lines", JsonSerializer.SerializeToElement(arguments));
        }
        var flag = changes[0];
        var enabled = flags[flag] switch
        {
            "on" => true, "off" => false,
            _ => throw new ArgumentException("--" + flag + " must be on or off.")
        };
        arguments["line"] = flag.Replace('-', '_');
        arguments["enabled"] = enabled;
        if (flags.TryGetValue("generation", out var generation))
        {
            if (!long.TryParse(generation, out var number) || number <= 0)
                throw new ArgumentException("--generation must be a positive session generation from lines/status.");
            arguments["sessionGeneration"] = number;
        }
        return new("set_control_line", JsonSerializer.SerializeToElement(arguments));
    }
}
