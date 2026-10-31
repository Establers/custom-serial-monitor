using System.Text.Json;
using System.Text;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI.Services;

public sealed class CommandSequenceFileService : ICommandSequenceFileService
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxFiles = 100;
    public const int MaxSteps = 10_000;
    public const int MaxBatchSteps = 10_000;
    public const int MaxBatchBytes = 16 * 1024 * 1024;

    private static readonly string[] Manual =
    [
        "Serial Monitor sequence format v1 (TX only), v3 (TX / RX Check). v2 attached ResponseCheck files are read and expanded into separate TX and RX Check steps.",
        "One UTF-8 JSON object: FormatVersion, nonblank Name, RepeatCount 1..9999, Steps array. Unknown/duplicate fields, comments and trailing commas are rejected. Import never executes a sequence.",
        "v3 steps have Kind: Tx or RxCheck. TX uses nonempty CommandText. RX Check uses RxPattern (1..4096 characters), with CommandText empty to prevent older clients from transmitting a pattern.",
        "Each step requires DelayAfterMs (0..600000) and LineEndingMode (null, None, Cr, Lf, Crlf). TX waits DelayAfterMs after sending. RX Check waits its whole DelayAfterMs window even if a pattern matches early. A missing pattern never branches or retries.",
        "Optional TxMode: null, Terminal, Hex. Null TX uses the main mode captured at run start. Null RX Check inherits the preceding enabled TX mode, or the main mode when no TX precedes it in that repeat. There is no separate response-format selector.",
        "Terminal TX uses UTF-8 plus ending; Terminal RX Check uses literal case-sensitive text in the connection RX encoding. HEX TX uses exact bytes; HEX RX Check accepts byte patterns such as AA 55 ?? 00. ?? ignores one byte; partial nibbles and all-wildcard patterns are invalid. Patterns span arbitrary receive chunks.",
        "RX Check also inspects RX already received since the preceding TX in the same repeat and connection. This prevents a fast response during the TX delay from being missed. RX-only checks start at their own activation. TX/RX timestamps refer to host write-start / receive-chunk times, not device-internal times.",
        "Optional IsEnabled defaults true; disabled rows and their waits are skipped. Optional Name and Comment are metadata only; leading/trailing pattern whitespace is significant. Line endings are ignored for HEX TX and RX Check.",
        "CaptureMs (0..60000, default 2000) and Expect {Kind:text|hex,Value:literal} retain their existing CLI/MCP fixed-capture semantics. They do not enable GUI checking. Legacy CLI/MCP rejects enabled RX Check or ResponseCheck steps, and cannot overwrite definitions containing them. It never sends an RX pattern as a command.",
        "GUI sequences containing enabled RX checks save bounded and paged results with raw RX and timestamps asynchronously. Without enabled RX checks, no sequence-result RX capture or storage is started and TX does not wait for result storage. Normal file logging is separate. No variables, conditions, checksum fields, sequence calls or automatic branching.",
        "Limits: 2 MiB per file, 100 files per Load, 10000 total steps and 16 MiB total input. All file/step limits are checked after v2 expansion. A bad file or duplicate sequence name rejects the whole import."
    ];

    public async Task<IReadOnlyList<CommandSequence>> LoadAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count > MaxFiles) throw new InvalidDataException($"Select at most {MaxFiles} files.");
        var result = new List<CommandSequence>();
        var totalSteps = 0;
        var totalBytes = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (stream.Length > MaxFileBytes) throw new InvalidDataException("File exceeds 2 MiB.");
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + count > MaxFileBytes) throw new InvalidDataException("File exceeds 2 MiB.");
                    totalBytes += count;
                    if (totalBytes > MaxBatchBytes) throw new InvalidDataException("Load exceeds 16 MiB total input.");
                    buffer.Write(chunk, 0, count);
                }
                // Stream JSON parsing accepts a UTF-8 BOM produced by Windows editors.
                buffer.Position = 0;
                using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
                var sequence = Parse(document.RootElement, cancellationToken, MaxBatchSteps - totalSteps);
                totalSteps += sequence.Steps.Count;
                if (!names.Add(sequence.Name)) throw new InvalidDataException($"Duplicate sequence name '{sequence.Name}'.");
                result.Add(sequence);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: {ex.Message}", ex);
            }
        }
        return result;
    }

    public async Task ExportAsync(string path, CommandSequence sequence, CancellationToken cancellationToken)
    {
        var json = Serialize(sequence);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static string Serialize(CommandSequence sequence)
    {
        sequence = ProfileSequenceMerge.Clone([sequence])[0];
        try { CommandSequenceMigration.ExpandResponseChecks(sequence); }
        catch (ArgumentException ex) { throw new InvalidDataException(ex.Message, ex); }
        var version = sequence.Steps.Any(step => step.Kind == CommandSequenceStepKind.RxCheck) ? 3 : 1;
        var json = JsonSerializer.Serialize(new
        {
            _manual = Manual,
            FormatVersion = version,
            sequence.Name,
            sequence.RepeatCount,
            Steps = sequence.Steps.Select(step =>
            {
                var fields = new Dictionary<string, object?>
                {
                    ["Name"] = step.Name, ["IsEnabled"] = step.IsEnabled, ["CommandText"] = step.StoredCommandText,
                    ["LineEndingMode"] = step.LineEndingMode?.ToString(), ["DelayAfterMs"] = step.DelayAfterMs,
                    ["Comment"] = step.Comment, ["TxMode"] = step.TxMode?.ToString(),
                    ["CaptureMs"] = step.CaptureMs, ["Expect"] = step.Expect
                };
                if (version == 3) fields["Kind"] = step.Kind.ToString();
                if (step.Kind == CommandSequenceStepKind.RxCheck) fields["RxPattern"] = step.CommandText;
                return fields;
            })
        }, new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidDataException("Export exceeds 2 MiB.");
        using var document = JsonDocument.Parse(json);
        Parse(document.RootElement); // Never export a file that our loader would reject.
        return json;
    }

    internal static CommandSequence Parse(JsonElement root, CancellationToken cancellationToken = default, int remainingSteps = MaxBatchSteps)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckObject(root, "$", ["_manual", "FormatVersion", "Name", "RepeatCount", "Steps"]);
        var format = Integer(root, "FormatVersion", "$", 1, 3);
        if (root.TryGetProperty("_manual", out var manual) &&
            (manual.ValueKind != JsonValueKind.Array || manual.EnumerateArray().Any(line => line.ValueKind != JsonValueKind.String)))
            throw new InvalidDataException("$._manual must be an array of strings.");
        var sequence = new CommandSequence
        {
            Name = RequiredText(root, "Name", "$" ).Trim(),
            RepeatCount = Integer(root, "RepeatCount", "$", 1, CommandSequence.MaxRepeatCount)
        };
        var steps = Required(root, "Steps", "$");
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() > MaxSteps)
            throw new InvalidDataException($"$.Steps must be an array with at most {MaxSteps} steps.");
        if (steps.GetArrayLength() > remainingSteps)
            throw new InvalidDataException($"Load exceeds {MaxBatchSteps} total steps.");
        foreach (var step in steps.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = $"$.Steps[{sequence.Steps.Count}]";
            CheckObject(step, path, format == 1
                ? ["Name", "IsEnabled", "CommandText", "LineEndingMode", "DelayAfterMs", "Comment", "TxMode", "CaptureMs", "Expect"]
                : format == 2 ? ["Name", "IsEnabled", "CommandText", "LineEndingMode", "DelayAfterMs", "Comment", "TxMode", "CaptureMs", "Expect", "ResponseCheck"]
                : ["Name", "IsEnabled", "Kind", "RxPattern", "CommandText", "LineEndingMode", "DelayAfterMs", "Comment", "TxMode", "CaptureMs", "Expect"]);
            var kind = CommandSequenceStepKind.Tx;
            if (step.TryGetProperty("Kind", out var kindValue))
            {
                if (kindValue.ValueKind != JsonValueKind.String || kindValue.GetString() is not ("Tx" or "RxCheck"))
                    throw new InvalidDataException(path + ".Kind must be Tx or RxCheck.");
                kind = kindValue.GetString() == "RxCheck" ? CommandSequenceStepKind.RxCheck : CommandSequenceStepKind.Tx;
            }
            var ending = Required(step, "LineEndingMode", path);
            TxLineEndingMode? mode = ending.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String when ending.GetString() == "None" => TxLineEndingMode.None,
                JsonValueKind.String when ending.GetString() == "Cr" => TxLineEndingMode.Cr,
                JsonValueKind.String when ending.GetString() == "Lf" => TxLineEndingMode.Lf,
                JsonValueKind.String when ending.GetString() == "Crlf" => TxLineEndingMode.Crlf,
                _ => throw new InvalidDataException($"{path}.LineEndingMode must be null, None, Cr, Lf or Crlf.")
            };
            var txMode = ParseTxMode(step, path);
            var command = Required(step, kind == CommandSequenceStepKind.RxCheck ? "RxPattern" : "CommandText", path);
            var transmit = Required(step, "CommandText", path);
            if (kind == CommandSequenceStepKind.Tx && step.TryGetProperty("RxPattern", out _))
                throw new InvalidDataException(path + ".RxPattern is valid only for RX Check.");
            if (kind == CommandSequenceStepKind.RxCheck && (transmit.ValueKind != JsonValueKind.String || transmit.GetString() != ""))
                throw new InvalidDataException(path + ".CommandText must be empty for RX Check; use RxPattern.");
            if (kind == CommandSequenceStepKind.RxCheck && command.ValueKind == JsonValueKind.String && command.GetString()!.Length > 4096)
                throw new InvalidDataException(path + ".RxPattern exceeds 4096 characters.");
            if (command.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(command.GetString()) ||
                (kind == CommandSequenceStepKind.Tx && txMode is null && string.IsNullOrWhiteSpace(command.GetString())))
                throw new InvalidDataException($"{path}.CommandText must be a nonempty string (legacy Global steps require nonblank text).");
            if (kind == CommandSequenceStepKind.RxCheck && txMode.HasValue)
            {
                try { new SerialMonitor.Core.Sequences.ResponseCheck(txMode == TxSendMode.Hex ? "hex" : "text", command.GetString()!).Validate(); }
                catch (ArgumentException ex) { throw new InvalidDataException(path + ": " + ex.Message, ex); }
            }
            sequence.Steps.Add(new CommandSequenceStep
            {
                Kind = kind,
                Name = NormalizeOptionalText(OptionalText(step, "Name", path)),
                IsEnabled = OptionalBoolean(step, "IsEnabled", path, defaultValue: true),
                CommandText = kind == CommandSequenceStepKind.Tx && txMode is null ? command.GetString()!.Trim() : command.GetString()!,
                LineEndingMode = mode,
                DelayAfterMs = Integer(step, "DelayAfterMs", path, 0, 600_000),
                Comment = NormalizeOptionalText(OptionalText(step, "Comment", path)),
                TxMode = txMode,
                CaptureMs = step.TryGetProperty("CaptureMs", out _) ? Integer(step, "CaptureMs", path, 0, 60000) : 2000,
                Expect = ParseExpectation(step, path),
                ResponseCheck = ParseResponseCheck(step, path),
                StepNumber = sequence.Steps.Count + 1
            });
        }
        CommandSequenceMigration.ExpandResponseChecks(sequence);
        if (sequence.Steps.Count > Math.Min(MaxSteps, remainingSteps)) throw new InvalidDataException("Expanded sequence exceeds the step limit.");
        return sequence;
    }

    private static SerialMonitor.Core.Sequences.ResponseCheck? ParseResponseCheck(JsonElement step, string path)
    {
        if (!step.TryGetProperty("ResponseCheck", out var check) || check.ValueKind == JsonValueKind.Null) return null;
        CheckObject(check, path + ".ResponseCheck", ["Kind", "Pattern", "TimeoutMs"]);
        var pattern = Required(check, "Pattern", path);
        if (pattern.ValueKind != JsonValueKind.String) throw new InvalidDataException(path + ".ResponseCheck.Pattern must be a string.");
        var result = new SerialMonitor.Core.Sequences.ResponseCheck(RequiredText(check, "Kind", path),
            pattern.GetString()!, Integer(check, "TimeoutMs", path, 1, 60000));
        try { result.Validate(); }
        catch (ArgumentException ex) { throw new InvalidDataException(path + ".ResponseCheck: " + ex.Message, ex); }
        return result;
    }

    private static TxSendMode? ParseTxMode(JsonElement step, string path)
    {
        if (!step.TryGetProperty("TxMode", out var mode) || mode.ValueKind == JsonValueKind.Null) return null;
        if (mode.ValueKind == JsonValueKind.String)
        {
            if (mode.GetString() == "Terminal") return TxSendMode.Terminal;
            if (mode.GetString() == "Hex") return TxSendMode.Hex;
        }
        throw new InvalidDataException($"{path}.TxMode must be null, Terminal or Hex.");
    }

    private static AutomationResponseExpectation? ParseExpectation(JsonElement step, string path)
    {
        if (!step.TryGetProperty("Expect", out var expect) || expect.ValueKind == JsonValueKind.Null) return null;
        CheckObject(expect, path + ".Expect", ["Kind", "Value"]);
        var kind = RequiredText(expect, "Kind", path + ".Expect");
        // Text whitespace is significant, including a literal newline or space.
        var valueElement = Required(expect, "Value", path + ".Expect");
        if (valueElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path}.Expect.Value must be a string.");
        var expectation = new AutomationResponseExpectation(kind, valueElement.GetString()!);
        try { _ = AutomationSequenceMatcher.PatternBytes(expectation, Encoding.UTF8); }
        catch (AutomationException ex) { throw new InvalidDataException($"{path}.Expect: {ex.Message}", ex); }
        return expectation;
    }

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool OptionalBoolean(JsonElement element, string key, string path, bool defaultValue)
    {
        if (!element.TryGetProperty(key, out var value)) return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{path}.{key} must be a boolean.")
        };
    }

    private static void CheckObject(JsonElement element, string path, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException($"{path}.{property.Name}: duplicate key.");
            if (!allowed.Contains(property.Name)) throw new InvalidDataException($"{path}.{property.Name}: unknown field.");
        }
    }

    private static JsonElement Required(JsonElement element, string key, string path) =>
        element.TryGetProperty(key, out var value) ? value : throw new InvalidDataException($"{path}.{key} is required.");

    private static int Integer(JsonElement element, string key, string path, int min, int max)
    {
        var value = Required(element, key, path);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new InvalidDataException($"{path}.{key} must be an integer between {min} and {max}.");
        return number;
    }

    private static string RequiredText(JsonElement element, string key, string path)
    {
        var value = Required(element, key, path);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{path}.{key} must be a nonblank string.");
        return value.GetString()!;
    }

    private static string? OptionalText(JsonElement element, string key, string path)
    {
        if (!element.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{path}.{key} must be a string or null.");
        return value.GetString();
    }
}
