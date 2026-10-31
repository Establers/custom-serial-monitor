using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SerialMonitor.Core.Sequences;

// Disk-only v2 format. The public/UI model remains complete. Each JSONL row is:
// [2, startTicksFromRunStart, durationTicks, rxStart, checkLength, rxLength, checkStatus, extras].
// Id/repeat/step come from steps.idx; immutable fields come from definition.json.
// All time deltas retain 100 ns precision. Extras preserve retries, errors,
// decoder context, timezone offsets and any deviation from the saved definition.
internal sealed class SequenceStepStorage(SequenceRunDefinition definition, DateTimeOffset origin)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly Dictionary<int, SequenceRunStep> _steps = definition.Steps.ToDictionary(s => s.Number,
        s => s with { Command = Preview(s.Command, 4096), TxHex = Preview(s.TxHex, 32768) });

    internal static string Preview(string text, int limit) => text.Length <= limit ? text : text[..limit] + "… [full TX: definition.json]";

    public byte[] Serialize(SequenceStepResult item)
    {
        _steps.TryGetValue(item.Step, out var step);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JsonOptions.Encoder }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(2);
            writer.WriteNumberValue(item.StartedAt.UtcTicks - origin.UtcTicks);
            writer.WriteNumberValue(item.EndedAt.UtcTicks - item.StartedAt.UtcTicks);
            writer.WriteNumberValue(item.RxStart);
            writer.WriteNumberValue(item.CheckRxEnd - item.RxStart);
            writer.WriteNumberValue(item.RxEnd - item.RxStart);
            writer.WriteNumberValue(SequenceRunStore.StatusCode(item.CheckStatus));
            writer.WriteStartObject();
            if (item.Attempt != 1) writer.WriteNumber("a", item.Attempt);
            if (item.SendStatus != (item.StepKind == "RxCheck" ? "not_requested" : "sent")) writer.WriteString("s", item.SendStatus);
            if (item.StepState != "completed") writer.WriteString("state", item.StepState);
            if (item.StartedAt.Offset != TimeSpan.Zero) writer.WriteNumber("o", (int)item.StartedAt.Offset.TotalMinutes);
            if (item.EndedAt.Offset != item.StartedAt.Offset) writer.WriteNumber("z", (int)item.EndedAt.Offset.TotalMinutes);
            if (item.MatchStart is { } match)
            {
                writer.WriteStartArray("m"); writer.WriteNumberValue(match - item.RxStart);
                writer.WriteNumberValue(item.MatchLength); writer.WriteEndArray();
            }
            else if (item.MatchLength != 0) writer.WriteNumber("ml", item.MatchLength);
            WriteTime(writer, "ma", "mo", item.MatchedAt, item.StartedAt);
            if (item.RelatedTxStep is { } related) writer.WriteNumber("r", related);
            WriteTime(writer, "rt", "ro", item.RelatedTxAt, item.StartedAt);
            if (item.ResponseMs is { } response) writer.WriteNumber("ms", response);
            if (item.Error is not null) writer.WriteString("e", item.Error);
            if (item.RxDecoderPrefix is not null) writer.WriteBase64String("p", item.RxDecoderPrefix);
            if (SequenceRunStore.StatusCode(item.CheckStatus) == 6) writer.WriteString("c", item.CheckStatus);

            _steps.TryGetValue(item.RelatedTxStep ?? -1, out var previous);
            var hasOverrides = false;
            Override("Command", item.Command, step?.Command ?? "");
            Override("TxHex", item.TxHex, step?.TxHex ?? "");
            Override("Pattern", item.Pattern, step?.ResponseCheck?.Pattern);
            Override("Kind", item.Kind, step?.ResponseCheck?.Kind);
            var timeout = step?.StepKind == "RxCheck" ? step.DelayMs : step?.ResponseCheck?.TimeoutMs ?? 0;
            if (item.TimeoutMs != timeout) { StartOverrides(); writer.WriteNumber("TimeoutMs", item.TimeoutMs); }
            Override("StepKind", item.StepKind, step?.StepKind ?? "Tx");
            Override("TxMode", item.TxMode, step?.TxMode);
            Override("RelatedTxHex", item.RelatedTxHex, previous?.TxHex);
            Override("RelatedTxCommand", item.RelatedTxCommand, previous?.Command);
            if (hasOverrides) writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();

            void StartOverrides()
            {
                if (hasOverrides) return;
                writer.WriteStartObject("x"); hasOverrides = true;
            }
            void Override(string key, string? value, string? baseline)
            {
                if (value == baseline) return;
                StartOverrides(); writer.WriteString(key, value);
            }
        }
        return buffer.WrittenSpan.ToArray();
    }

    public SequenceStepResult Deserialize(JsonElement row, long id, int repeat, int number)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 8 || row[0].GetInt32() != 2)
            throw new InvalidDataException("Unsupported sequence result format.");
        _steps.TryGetValue(number, out var step);
        var extra = row[7];
        extra.TryGetProperty("x", out var changes);
        var offset = TimeSpan.FromMinutes(Value(extra, "o", 0));
        var start = new DateTimeOffset(origin.UtcTicks + row[1].GetInt64(), TimeSpan.Zero).ToOffset(offset);
        var end = new DateTimeOffset(start.UtcTicks + row[2].GetInt64(), TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(Value(extra, "z", (int)offset.TotalMinutes)));
        var rxStart = row[3].GetInt64();
        var stepKind = Value(changes, "StepKind", step?.StepKind ?? "Tx");
        var related = Value<int?>(extra, "r", null);
        _steps.TryGetValue(related ?? -1, out var previous);
        var hasMatch = extra.TryGetProperty("m", out var match);
        var status = row[6].GetInt32() switch
        {
            1 => "matched", 2 => "not_matched", 3 => "not_requested", 4 => "canceled", 5 => "inconclusive",
            6 => extra.GetProperty("c").GetString()!,
            _ => throw new InvalidDataException("Invalid sequence check status.")
        };
        return new(id, repeat, number, Value(extra, "a", 1),
            Value(changes, "Command", step?.Command ?? ""),
            Value(changes, "TxHex", step?.TxHex ?? ""),
            Value(extra, "s", stepKind == "RxCheck" ? "not_requested" : "sent"), status,
            Value(changes, "Pattern", step?.ResponseCheck?.Pattern), Value(changes, "Kind", step?.ResponseCheck?.Kind),
            Value(changes, "TimeoutMs", step?.StepKind == "RxCheck" ? step.DelayMs : step?.ResponseCheck?.TimeoutMs ?? 0),
            start, end, Value<double?>(extra, "ms", null), rxStart, rxStart + row[4].GetInt64(), rxStart + row[5].GetInt64(),
            hasMatch ? rxStart + match[0].GetInt64() : null, hasMatch ? match[1].GetInt32() : Value(extra, "ml", 0),
            Value<string?>(extra, "e", null), Value(extra, "state", "completed"), stepKind,
            ReadTime(extra, "ma", "mo", start), related, ReadTime(extra, "rt", "ro", start),
            Value(changes, "RelatedTxHex", previous?.TxHex),
            Value(changes, "RelatedTxCommand", previous?.Command),
            Value(changes, "TxMode", step?.TxMode), Value<byte[]?>(extra, "p", null));
    }

    private static T Value<T>(JsonElement obj, string key, T fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) ? value.Deserialize<T>()! : fallback;

    private static void WriteTime(Utf8JsonWriter writer, string key, string offsetKey, DateTimeOffset? value, DateTimeOffset start)
    {
        if (value is not { } time) return;
        writer.WriteNumber(key, time.UtcTicks - start.UtcTicks);
        if (time.Offset != start.Offset) writer.WriteNumber(offsetKey, (int)time.Offset.TotalMinutes);
    }

    private static DateTimeOffset? ReadTime(JsonElement obj, string key, string offsetKey, DateTimeOffset start) =>
        obj.TryGetProperty(key, out var value)
            ? new DateTimeOffset(start.UtcTicks + value.GetInt64(), TimeSpan.Zero)
                .ToOffset(TimeSpan.FromMinutes(Value(obj, offsetKey, (int)start.Offset.TotalMinutes))) : null;
}
