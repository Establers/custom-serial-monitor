using System.Text;
using System.Text.Json;

namespace SerialMonitor.Core.Automation;

public sealed record AutomationResponseExpectation(string Kind, string Value);
public sealed record AutomationSequenceStep(string? Name, string Command, string TxMode = "text",
    string LineEnding = "crlf", int CaptureMs = 2000, int DelayAfterMs = 300,
    AutomationResponseExpectation? Expect = null);
public sealed record AutomationSequenceDefinition(string Name, int RepeatCount,
    IReadOnlyList<AutomationSequenceStep> Steps);
public sealed record AutomationSequenceStepResult(int StepIndex, int RepeatIndex, int DefinitionStepIndex,
    string? Name, string SendStatus, long RxStartOffset, long RxEndOffset, string MatchStatus, string? Error = null);
public sealed record AutomationSequenceRun(string RunId, string Name, string State, int StepIndex,
    int CompletedSteps, int TotalSteps, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Error,
    string Port, long SessionGeneration, IReadOnlyList<AutomationSequenceStepResult> Steps, CaptureResult? Capture);

public static class AutomationSequenceValidation
{
    public const int MaxDefinitionBytes = 48 * 1024;
    public const int MaxExecutedSteps = 128;
    public const int MaxWaitMs = 600000;

    public static AutomationSequenceDefinition Parse(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(definition.GetRawText()) > MaxDefinitionBytes)
            throw Invalid("A sequence object of at most 48 KiB UTF-8 JSON is required.");
        CheckProperties(definition, ["name", "repeatCount", "steps"], ["name", "repeatCount", "steps"]);
        RequireKind(definition.GetProperty("name"), JsonValueKind.String, "name");
        RequireInteger(definition.GetProperty("repeatCount"), "repeatCount");
        RequireKind(definition.GetProperty("steps"), JsonValueKind.Array, "steps");
        foreach (var step in definition.GetProperty("steps").EnumerateArray())
        {
            CheckProperties(step, ["name", "command", "txMode", "lineEnding", "captureMs", "delayAfterMs", "expect"], ["command"]);
            foreach (var key in new[] { "name", "command", "txMode", "lineEnding" })
                if (step.TryGetProperty(key, out var value) && !(key == "name" && value.ValueKind == JsonValueKind.Null))
                    RequireKind(value, JsonValueKind.String, key);
            foreach (var key in new[] { "captureMs", "delayAfterMs" })
                if (step.TryGetProperty(key, out var value)) RequireInteger(value, key);
            if (step.TryGetProperty("expect", out var expect) && expect.ValueKind != JsonValueKind.Null)
            {
                CheckProperties(expect, ["kind", "value"], ["kind", "value"]);
                RequireKind(expect.GetProperty("kind"), JsonValueKind.String, "expect.kind");
                RequireKind(expect.GetProperty("value"), JsonValueKind.String, "expect.value");
            }
        }
        return Validate(definition.Deserialize<AutomationSequenceDefinition>(AutomationJson.Options)
            ?? throw Invalid("A sequence definition is required."));
    }

    public static AutomationSequenceDefinition Validate(AutomationSequenceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 128 ||
            definition.RepeatCount is < 1 or > MaxExecutedSteps || definition.Steps is null ||
            definition.Steps.Count is < 1 or > MaxExecutedSteps ||
            (long)definition.RepeatCount * definition.Steps.Count > MaxExecutedSteps)
            throw Invalid("Use a name of 1..128 characters, repeatCount 1..128 and at most 128 executed steps including repeats.");
        long wait = 0;
        foreach (var step in definition.Steps)
        {
            if (step is null || step.Name?.Length > 128 || string.IsNullOrEmpty(step.Command) || step.Command.Length > 16384 ||
                step.TxMode is not ("text" or "hex") || step.LineEnding is not ("none" or "cr" or "lf" or "crlf") ||
                step.CaptureMs is < 0 or > 60000 || step.DelayAfterMs is < 0 or > MaxWaitMs)
                throw Invalid("Check step name (0..128), command (1..16384), txMode (text|hex), lineEnding, captureMs (0..60000) and delayAfterMs (0..600000).");
            if (step.Expect is { } expect)
                _ = AutomationSequenceMatcher.PatternBytes(expect, Encoding.UTF8);
            wait += (long)step.CaptureMs + step.DelayAfterMs;
        }
        if (wait * definition.RepeatCount > MaxWaitMs)
            throw Invalid("The total capture and delay time including repeats must not exceed 600000ms.");
        var frozen = definition with { Name = definition.Name.Trim(), Steps = definition.Steps.Select(step => step with { Name = step.Name?.Trim() }).ToArray() };
        if (JsonSerializer.SerializeToUtf8Bytes(frozen, AutomationJson.Options).Length > MaxDefinitionBytes)
            throw Invalid("A sequence definition must not exceed 48 KiB UTF-8 JSON.");
        return frozen;
    }

    private static AutomationException Invalid(string message) => new("invalid_argument", message);

    private static void CheckProperties(JsonElement value, string[] allowed, string[] required)
    {
        RequireKind(value, JsonValueKind.Object, "definition/step/expect");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Invalid("Unknown, misspelled or duplicate sequence property: " + property.Name);
        }
        foreach (var key in required)
            if (!seen.Contains(key)) throw Invalid("Missing sequence property: " + key);
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, string key)
    {
        if (value.ValueKind != kind) throw Invalid(key + " has an invalid JSON type.");
    }

    private static void RequireInteger(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _))
            throw Invalid(key + " must be a 32-bit JSON integer.");
    }
}
