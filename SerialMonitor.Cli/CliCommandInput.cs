using System.Text;

namespace SerialMonitor.Cli;

public static class CliCommandInput
{
    public static void AddArguments(IReadOnlyDictionary<string, string> flags, IDictionary<string, object?> arguments)
    {
        if (new[] { "text", "text-file", "hex" }.Count(flags.ContainsKey) > 1)
            throw new ArgumentException("Choose exactly one of --text, --text-file or --hex.");
        if (flags.TryGetValue("text", out var text))
        { arguments["command"] = text; arguments["txMode"] = "text"; }
        if (flags.TryGetValue("text-file", out var path))
        { arguments["command"] = ReadTextFile(path); arguments["txMode"] = "text"; }
        if (flags.TryGetValue("hex", out var hex))
        { arguments["command"] = hex; arguments["txMode"] = "hex"; }
        if (arguments.TryGetValue("command", out var command))
            _ = new UTF8Encoding(false, true).GetByteCount((string)command!);
    }

    public static string ReadTextFile(string path)
    {
        // Bound the read itself, even if another process grows/replaces the file.
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), false);
        // Accept an optional UTF-8 BOM, without auto-detecting UTF-16/32 or
        // switching to a decoder that silently replaces malformed characters.
        if (reader.Peek() == '\uFEFF') reader.Read();
        var buffer = new char[16385];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = reader.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count is 0 or > 16384)
            throw new ArgumentException("Command file must contain 1..16384 characters. Its whitespace and final newline are preserved.");
        return new string(buffer, 0, count);
    }
}
