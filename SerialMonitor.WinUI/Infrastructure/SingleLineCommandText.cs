using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class SingleLineCommandText
{
    public static bool HasLineBreaks(string text) => text.IndexOfAny(['\r', '\n', '\f', '\u0085', '\u2028', '\u2029']) >= 0;

    // Preserve every line's content when opening an old TX or RX pattern for review. Never
    // silently keep only its first line or retain an invisible carriage return.
    public static string ForEditing(string text) => text.ReplaceLineEndings(" ");

    public static bool IsMultilineTerminalTx(CommandSequenceStep step, TxSendMode defaultMode) =>
        step.Kind == CommandSequenceStepKind.Tx && (step.TxMode ?? defaultMode) == TxSendMode.Terminal && HasLineBreaks(step.CommandText);
}
