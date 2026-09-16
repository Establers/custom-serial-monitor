using System.Text.Json;
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
        "Unknown fields, duplicate keys, wrong types, missing required fields and out-of-range values fail the entire import. Sequence names must be unique ignoring case, including existing sequences.",
        "Steps execute in array order. DelayAfterMs is the wait AFTER sending, including the last step. RepeatCount repeats the entire sequence. Comment and _manual never execute.",
        "LineEndingMode: None adds nothing; Cr adds CR; Lf adds LF; Crlf adds CR+LF; null uses the main TX ending at run start. JSON Global is invalid.",
        "Execution uses the main Terminal/HEX mode at run start; mode is not stored in this file. Terminal sends UTF-8 plus ending. HEX sends bytes such as AA 55 01; endings are ignored, use None. Do not mix modes.",
        "Command leading/trailing whitespace is trimmed by the app. For exact whitespace bytes use HEX. Escape quotes and backslashes using JSON string escaping.",
        "No response wait, conditions, variables, retry fields, checksum calculation or sequence calls. Never invent command bytes or unsupported fields; ask for the device protocol when unclear.",
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
                step.DelayAfterMs, step.Comment
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
            CheckObject(step, path, ["Name", "CommandText", "LineEndingMode", "DelayAfterMs", "Comment"]);
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
            sequence.Steps.Add(new CommandSequenceStep
            {
                Name = NormalizeOptionalText(OptionalText(step, "Name", path)),
                CommandText = RequiredText(step, "CommandText", path).Trim(),
                LineEndingMode = mode,
                DelayAfterMs = Integer(step, "DelayAfterMs", path, 0, 600_000),
                Comment = NormalizeOptionalText(OptionalText(step, "Comment", path)),
                StepNumber = sequence.Steps.Count + 1
            });
        }
        return sequence;
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
