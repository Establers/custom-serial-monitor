using System.Text.Json;
using System.Text;
using SerialMonitor.Core.Automation;
using SerialMonitor.WinUI.Models;

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
        "Serial Monitor sequence format v1. One UTF-8 JSON file contains one sequence. Select multiple files with Load.",
        "Required root fields (case-sensitive): FormatVersion (integer 1), Name (nonblank string), RepeatCount (integer 1..9999), Steps (array, 0..10000 steps). Empty sequences can be edited but cannot run.",
        "Optional root _manual is an array of strings for documentation only. JSON // and /* */ comments and trailing commas are forbidden.",
        "Each step requires CommandText (nonblank string), DelayAfterMs (integer 0..600000), LineEndingMode (null or exactly None, Cr, Lf, Crlf). Optional Name and Comment are strings or null.",
        "Step Name is an optional short label for GUI progress/errors and CLI/MCP step results; blank names use CommandText in the GUI. Step names need not be unique. Comment is a reference note shown in the GUI step tooltip and saved in JSON. Neither field is transmitted or changes execution.",
        "Unknown fields, duplicate keys, wrong types, missing required fields and out-of-range values fail the entire import. Sequence names must be unique ignoring case, including existing sequences.",
        "Steps execute in array order. DelayAfterMs is the wait AFTER sending, including the last step. RepeatCount repeats the entire sequence. Comment and _manual never execute.",
        "LineEndingMode: None adds nothing; Cr adds CR; Lf adds LF; Crlf adds CR+LF; null uses the main TX ending at run start. JSON Global is invalid.",
        "Optional step TxMode is null (use main mode at run start), Terminal or Hex. Legacy files without TxMode use the main mode. Terminal sends UTF-8 plus ending; HEX sends exact bytes such as AA 55 01 and ignores endings. All HEX steps are validated before any TX.",
        "Optional CaptureMs is an integer 0..60000 (default 2000). Optional Expect is null or {Kind: text|hex, Value: literal nonempty string, maximum 4096 characters}. Kind and Value are case-sensitive. HEX expectations contain complete bytes with optional whitespace. No regex or subsequence matching.",
        "CaptureMs and Expect are automation metadata for CLI/MCP sequence execution only. Ordinary GUI Run sends steps using TxMode and DelayAfterMs, without RX capture or matching. A missing literal does not establish product failure when printf output interleaves; automation retains all RX for inspection.",
        "Command leading/trailing whitespace is preserved when TxMode is explicitly Terminal or Hex. Legacy steps with null/missing TxMode keep the GUI's trimming behavior. Escape quotes and backslashes using JSON string escaping.",
        "No conditions, variables, retry fields, checksum calculation or sequence calls. Never invent command bytes or unsupported fields; ask for the device protocol when unclear.",
        "Import never runs a sequence. Review commands and select the correct connection and mode before Run. Export includes only this sequence, not connection settings or RX rules.",
        "Limits: 2 MiB per file, 100 files per Load, at most 10000 total steps and 16 MiB total input per Load. Any invalid file or name collision cancels the whole batch. Rename Name explicitly to import another copy."
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
        var json = JsonSerializer.Serialize(new
        {
            _manual = Manual,
            FormatVersion = 1,
            sequence.Name,
            sequence.RepeatCount,
            Steps = sequence.Steps.Select(step => new
            {
                step.Name, step.CommandText,
                LineEndingMode = step.LineEndingMode?.ToString(),
                step.DelayAfterMs, step.Comment,
                TxMode = step.TxMode?.ToString(), step.CaptureMs, step.Expect
            })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes) throw new InvalidDataException("Export exceeds 2 MiB.");
        using var document = JsonDocument.Parse(json);
        Parse(document.RootElement); // Never export a file that our loader would reject.
        return json;
    }

    internal static CommandSequence Parse(JsonElement root, CancellationToken cancellationToken = default, int remainingSteps = MaxBatchSteps)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckObject(root, "$", ["_manual", "FormatVersion", "Name", "RepeatCount", "Steps"]);
        if (Integer(root, "FormatVersion", "$", 1, 1) != 1) throw new InvalidDataException("Unsupported format.");
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
            CheckObject(step, path, ["Name", "CommandText", "LineEndingMode", "DelayAfterMs", "Comment", "TxMode", "CaptureMs", "Expect"]);
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
            var command = Required(step, "CommandText", path);
            if (command.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(command.GetString()) ||
                (txMode is null && string.IsNullOrWhiteSpace(command.GetString())))
                throw new InvalidDataException($"{path}.CommandText must be a nonempty string (legacy Global steps require nonblank text).");
            sequence.Steps.Add(new CommandSequenceStep
            {
                Name = NormalizeOptionalText(OptionalText(step, "Name", path)),
                CommandText = txMode is null ? command.GetString()!.Trim() : command.GetString()!,
                LineEndingMode = mode,
                DelayAfterMs = Integer(step, "DelayAfterMs", path, 0, 600_000),
                Comment = NormalizeOptionalText(OptionalText(step, "Comment", path)),
                TxMode = txMode,
                CaptureMs = step.TryGetProperty("CaptureMs", out _) ? Integer(step, "CaptureMs", path, 0, 60000) : 2000,
                Expect = ParseExpectation(step, path),
                StepNumber = sequence.Steps.Count + 1
            });
        }
        return sequence;
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
