using System.Globalization;

namespace SerialMonitor.Core.Sequences;

public sealed record SequenceRunStep(int Number, string Command, string TxMode, string LineEnding,
    int DelayMs, ResponseCheck? ResponseCheck, string TxHex, string StepKind = "Tx");
public sealed record SequenceRunDefinition(string Name, int RepeatCount, string Port, string Encoding,
    IReadOnlyList<SequenceRunStep> Steps);
public sealed record SequenceRunSummary(string RunId, string Name, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string State, int Sent, int Matched, int NotMatched, int NotChecked,
    int Inconclusive, int Attempts, long RxBytes, string? Error = null,
    int? CurrentRepeat = null, int? CurrentStep = null, string? Phase = null, string Encoding = "Utf8",
    int TotalRepeats = 0, int CompletedRepeats = 0, int MatchedRepeats = 0, int MissedRepeats = 0,
    int UncertainRepeats = 0, int UncheckedRepeats = 0)
{
    public string DisplayText => $"{StartedAt:yyyy-MM-dd HH:mm:ss} · {Name} · {State}";
}
public sealed record SequenceStepResult(long Id, int Repeat, int Step, int Attempt, string Command,
    string TxHex, string SendStatus, string CheckStatus, string? Pattern, string? Kind,
    int TimeoutMs, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double? ResponseMs,
    long RxStart, long CheckRxEnd, long RxEnd, long? MatchStart, int MatchLength, string? Error,
    string StepState = "completed", string StepKind = "Tx", DateTimeOffset? MatchedAt = null,
    int? RelatedTxStep = null, DateTimeOffset? RelatedTxAt = null, string? RelatedTxHex = null,
    string? RelatedTxCommand = null, string? TxMode = null, byte[]? RxDecoderPrefix = null)
{
    public string KindText => StepKind == "RxCheck" ? "RX Check" : "TX";
    public string TimeText => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string ResultStatus => StepKind == "RxCheck" || Pattern is not null ? CheckStatus : SendStatus;
    public string DisplayText => $"{Repeat,5}  #{Step,-4} {SendStatus,-8} {CheckStatus,-14} {Command.Replace('\r', ' ').Replace('\n', ' ')}";
}
public sealed record SequenceResultPage(IReadOnlyList<SequenceStepResult> Rows, long NextCursor, bool HasMore);
public sealed record SequenceRxSlice(long Offset, byte[] Bytes, DateTimeOffset? ReceivedAt, long Timestamp = 0, long Generation = 0);
