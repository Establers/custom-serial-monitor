namespace SerialMonitor.WinUI.Infrastructure;

// One registry is shared by capture, validation, native dispatch and WebView2.
// Context-specific actions remain reserved even when their control lacks focus.
internal static class ShortcutReservations
{
    public static IReadOnlyCollection<string> Keys => AppShortcuts.Catalog.ReservedKeys;
    public static Reservation? Find(string? shortcut) => AppShortcuts.Catalog.Find(shortcut);

    internal sealed record Reservation(string ResourceId, string Action)
    {
        public string FormatUiError(string shortcut) => UiText.Format("ShortcutCaptureReserved",
            "{0} is already used for {1}. Choose another key.", shortcut, UiText.Get(ResourceId, Action));
    }
}
