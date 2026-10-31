using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.System;

namespace SerialMonitor.WinUI.Infrastructure;

internal enum AppShortcutAction
{
    EditorConfirm, EditorCancel, SearchCancel, SearchSubmitNext, SearchSubmitPrevious,
    CommandSubmit, ListActivate, HistoryPrevious, HistoryNext, SearchFocus,
    SearchNext, SearchPrevious, SearchCase, SearchWord, SearchRegex, GoTo, Marker,
    Copy, AutoScroll, FocusNavigation, FocusNext, TextNavigation, TextEditing, BrowserControls, WindowControls
}

internal static class AppShortcuts
{
    public static ShortcutCatalog Catalog { get; } = ShortcutCatalog.ParseAsset(ReadAsset());

    public static bool Matches(AppShortcutAction action, VirtualKey key) =>
        Catalog.Matches(action, key, SavedCommandShortcut.ReadModifiers());

    internal static string ReadAsset()
    {
        using var stream = typeof(AppShortcuts).Assembly.GetManifestResourceStream("SerialMonitor.AppShortcuts")
            ?? throw new InvalidOperationException("Missing shared shortcut catalog.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal sealed class ShortcutCatalog
{
    internal const string AssetPrefix = "globalThis.serialMonitorShortcutDefinitions = ";
    private readonly Dictionary<AppShortcutAction, HashSet<(VirtualKey, VirtualKeyModifiers)>> _bindings = new();
    private readonly Dictionary<string, ShortcutReservations.Reservation> _reservations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ReservedKeys => _reservations.Keys;
    public IEnumerable<(AppShortcutAction Action, VirtualKey Key, VirtualKeyModifiers Modifiers)> Bindings =>
        _bindings.SelectMany(pair => pair.Value.Select(gesture => (pair.Key, gesture.Item1, gesture.Item2)));

    public bool Matches(AppShortcutAction action, VirtualKey key, VirtualKeyModifiers modifiers) =>
        _bindings[action].Contains((SavedCommandShortcut.NormalizePhysicalKey(key), modifiers));

    public ShortcutReservations.Reservation? Find(string? shortcut) =>
        SavedCommandShortcut.TryParseGesture(shortcut, out var key, out var modifiers)
            ? _reservations.GetValueOrDefault(SavedCommandShortcut.CreateGestureText(key, modifiers)!) : null;

    internal static ShortcutCatalog ParseAsset(string asset)
    {
        var text = asset.Trim();
        if (!text.StartsWith(AssetPrefix, StringComparison.Ordinal) || !text.EndsWith(';'))
            throw new InvalidDataException("Shortcut catalog must be a data-only JSON assignment.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter<AppShortcutAction>());
        var definitions = JsonSerializer.Deserialize<Definition[]>(text[AssetPrefix.Length..^1], options)
            ?? throw new InvalidDataException("Empty shortcut catalog.");
        var catalog = new ShortcutCatalog();
        foreach (var definition in definitions)
        {
            if (!Enum.IsDefined(definition.Action) || definition.Gestures.Length == 0 ||
                string.IsNullOrWhiteSpace(definition.ResourceId) || string.IsNullOrWhiteSpace(definition.Description) ||
                !catalog._bindings.TryAdd(definition.Action, new()))
                throw new InvalidDataException($"Invalid or duplicate shortcut action: {definition.Action}.");
            var ignored = VirtualKeyModifiers.None;
            foreach (var modifier in definition.IgnoreModifiers)
            {
                if (!SavedCommandShortcut.TryParseModifier(modifier, out var flag))
                    throw new InvalidDataException($"Invalid shortcut modifier: {modifier}.");
                ignored |= flag;
            }
            foreach (var gesture in definition.Gestures)
            {
                if (!SavedCommandShortcut.TryParseGesture(gesture, out var key, out var required) || (required & ignored) != 0)
                    throw new InvalidDataException($"Invalid shortcut gesture: {gesture}.");
                // Expand modifier variants once, rather than maintaining separate
                // matching and reservation lists or checking wildcards per keypress.
                for (var bits = 0; bits < 16; bits++)
                {
                    var modifiers = (VirtualKeyModifiers)bits;
                    var matched = modifiers;
                    if (definition.AcceptMetaAsControl && (matched & VirtualKeyModifiers.Windows) != 0)
                        matched = (matched & ~VirtualKeyModifiers.Windows) | VirtualKeyModifiers.Control;
                    if ((matched & ~ignored) != required) continue;
                    catalog._bindings[definition.Action].Add((key, modifiers));
                    catalog._reservations.TryAdd(SavedCommandShortcut.CreateGestureText(key, modifiers)!,
                        new(definition.ResourceId, definition.Description));
                }
            }
        }
        if (catalog._bindings.Count != Enum.GetValues<AppShortcutAction>().Length)
            throw new InvalidDataException("Every shortcut action must have a catalog definition.");
        return catalog;
    }

    private sealed class Definition
    {
        public required AppShortcutAction Action { get; init; }
        public required string[] Gestures { get; init; }
        public string[] IgnoreModifiers { get; init; } = [];
        public bool AcceptMetaAsControl { get; init; }
        public required string ResourceId { get; init; }
        public required string Description { get; init; }
    }
}
