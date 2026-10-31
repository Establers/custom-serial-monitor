using System.Buffers;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

// Disk-only v2: [2, startUtcTicks, durationTicks, port, generation, requestedMs,
// receivedBytes, extras]. ID and file paths derive from the metadata filename.
// Public CLI/MCP capture fields are reconstructed in full, including zero counts.
internal static class CaptureResultStorage
{
    public static byte[] Serialize(CaptureResult result, string directory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = AutomationJson.Options.Encoder }))
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(2);
            writer.WriteNumberValue(result.StartedAt.UtcTicks);
            writer.WriteNumberValue(result.EndedAt.UtcTicks - result.StartedAt.UtcTicks);
            writer.WriteStringValue(result.Port);
            writer.WriteNumberValue(result.SessionGeneration);
            writer.WriteNumberValue(result.RequestedDurationMs);
            writer.WriteNumberValue(result.ReceivedBytes);
            writer.WriteStartObject();
            if (result.CaptureStatus != "completed") writer.WriteString("status", result.CaptureStatus);
            if (result.SendStatus != "not_requested") writer.WriteString("send", result.SendStatus);
            if (result.Command is not null) writer.WriteString("command", result.Command);
            if (result.StoredBytes != result.ReceivedBytes) writer.WriteNumber("stored", result.StoredBytes);
            if (result.DroppedBytes != 0) writer.WriteNumber("dropped", result.DroppedBytes);
            if (result.SerialErrors != 0) writer.WriteNumber("errors", result.SerialErrors);
            if (result.RxGapDetected) writer.WriteBoolean("gap", true);
            if (result.Error is not null) writer.WriteString("error", result.Error);
            if (result.TxMode != "text") writer.WriteString("mode", result.TxMode);
            if (result.LineEnding != "crlf") writer.WriteString("ending", result.LineEnding);
            if (result.PreviewFormat is not null) writer.WriteString("preview", result.PreviewFormat);
            if (result.StartedAt.Offset != TimeSpan.Zero) writer.WriteNumber("offset", (int)result.StartedAt.Offset.TotalMinutes);
            if (result.EndedAt.Offset != result.StartedAt.Offset) writer.WriteNumber("endOffset", (int)result.EndedAt.Offset.TotalMinutes);
            if (result.RawFile != Path.Combine(directory, result.CaptureId + ".bin")) writer.WriteString("raw", result.RawFile);
            if (result.TextFile != Path.Combine(directory, result.CaptureId + ".txt")) writer.WriteString("text", result.TextFile);
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static CaptureResult Deserialize(JsonElement row, string directory, string id)
    {
        if (row.ValueKind == JsonValueKind.Object)
            return row.Deserialize<CaptureResult>(AutomationJson.Options) ?? throw new InvalidDataException("Invalid capture metadata.");
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 8 || row[0].GetInt32() != 2)
            throw new InvalidDataException("Unsupported capture metadata format.");
        var extra = row[7];
        var offset = TimeSpan.FromMinutes(Value(extra, "offset", 0));
        var start = new DateTimeOffset(row[1].GetInt64(), TimeSpan.Zero).ToOffset(offset);
        var end = new DateTimeOffset(start.UtcTicks + row[2].GetInt64(), TimeSpan.Zero)
            .ToOffset(TimeSpan.FromMinutes(Value(extra, "endOffset", (int)offset.TotalMinutes)));
        var received = row[6].GetInt64();
        return new(id, Value(extra, "status", "completed"), Value(extra, "send", "not_requested"),
            Value<string?>(extra, "command", null), row[3].GetString()!, row[4].GetInt64(), start, end, row[5].GetInt32(),
            received, Value(extra, "stored", received), Value(extra, "dropped", 0L), Value(extra, "errors", 0L),
            Value(extra, "gap", false), Value<string?>(extra, "error", null),
            Value(extra, "raw", Path.Combine(directory, id + ".bin")), Value(extra, "text", Path.Combine(directory, id + ".txt")),
            Value(extra, "mode", "text"), Value(extra, "ending", "crlf"), Value<string?>(extra, "preview", null));
    }

    private static T Value<T>(JsonElement obj, string key, T fallback) =>
        obj.TryGetProperty(key, out var value) ? value.Deserialize<T>()! : fallback;
}
