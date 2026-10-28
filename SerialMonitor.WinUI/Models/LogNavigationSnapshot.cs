using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Models;

public sealed class LogNavigationSnapshot
{
    public int Count { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? StartTime { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? EndTime { get; init; }
    public string? EndId { get; init; }
    public int CurrentIndex { get; init; } = -1;
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? CurrentTimestamp { get; init; }
    public bool CanGoBack { get; init; }
    public bool Busy { get; init; }
    public bool Ordered { get; init; } = true;
    public IReadOnlyList<double> Bins { get; init; } = Array.Empty<double>();
    public double Peak { get; init; }
    public double BinMilliseconds { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? HistogramStartTime { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? HistogramEndTime { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? ViewportStartTime { get; init; }
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? ViewportEndTime { get; init; }
}

public sealed class LogNavigationResult
{
    public bool Ok { get; init; }
    public bool? CanGoBack { get; init; }
    public int Index { get; init; } = -1;
    [JsonConverter(typeof(LogNavigationTimestampJsonConverter))]
    public long? Timestamp { get; init; }
    public string? Id { get; init; }
    public string? Error { get; init; }
}

// WebView JSON can represent an integral JS Number using decimal or exponent
// notation. Parse the exact token instead of rounding through double/decimal.
public sealed class LogNavigationTimestampJsonConverter : JsonConverter<long?>
{
    private const long MinimumTimestamp = -62_135_596_800_000;
    private const long MaximumTimestamp = 253_402_300_799_999;

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a numeric Unix millisecond timestamp.");
        if (reader.TryGetInt64(out var integer)) return Validate(integer);

        var tokenLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (tokenLength > 128) throw new JsonException("Timestamp number is too long.");
        var token = reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
            : Encoding.UTF8.GetString(reader.ValueSpan);
        var exponentIndex = token.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(token.AsSpan(exponentIndex + 1),
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw new JsonException("Timestamp exponent is outside the supported range.");
        var mantissa = exponentIndex < 0 ? token : token[..exponentIndex];
        var negative = mantissa.StartsWith('-');
        if (negative) mantissa = mantissa[1..];
        var decimalPoint = mantissa.IndexOf('.');
        var decimalPlaces = decimalPoint < 0 ? 0 : mantissa.Length - decimalPoint - 1;
        var digits = mantissa.Replace(".", string.Empty).TrimStart('0');
        if (digits.Length == 0) return 0;
        var shift = (long)exponent - decimalPlaces;
        if (shift < 0)
        {
            var removed = -shift;
            if (removed > digits.Length) throw new JsonException("Timestamp has fractional milliseconds.");
            var integralLength = digits.Length - (int)removed;
            for (var index = integralLength; index < digits.Length; index++)
                if (digits[index] != '0') throw new JsonException("Timestamp has fractional milliseconds.");
            digits = digits[..integralLength];
            shift = 0;
        }
        // Every supported DateTimeOffset Unix millisecond value has <=15 digits.
        if (digits.Length + shift > 15) throw new JsonException("Timestamp is outside the supported date range.");
        if (shift > 0) digits += new string('0', (int)shift);
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out integer))
            throw new JsonException("Timestamp is outside the supported range.");
        return Validate(negative ? -integer : integer);
    }

    private static long Validate(long value) => value >= MinimumTimestamp && value <= MaximumTimestamp
        ? value : throw new JsonException("Timestamp is outside the supported date range.");

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(Validate(value.Value));
        else writer.WriteNullValue();
    }
}
