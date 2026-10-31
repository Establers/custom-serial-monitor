using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class SavedCommandShortcut
{
    // Recording and dispatch must accept the same physical key combinations.
    public static string? CreateText(VirtualKey key)
        => CreateText(key, ReadModifiers());

    internal static VirtualKeyModifiers ReadModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (IsDown(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (IsDown(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    internal static bool IsFunctionKey(VirtualKey key) => key is >= VirtualKey.F1 and <= VirtualKey.F24;

    internal static bool IsModifierKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.Shift or VirtualKey.LeftShift
        or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    internal static VirtualKey NormalizePhysicalKey(VirtualKey key) => key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9
        ? VirtualKey.Number0 + (key - VirtualKey.NumberPad0) : key;

    internal static bool TryParseModifier(string text, out VirtualKeyModifiers flag)
    {
        flag = text.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => VirtualKeyModifiers.Control,
            "ALT" or "MENU" => VirtualKeyModifiers.Menu,
            "SHIFT" => VirtualKeyModifiers.Shift,
            "WIN" or "WINDOWS" or "META" => VirtualKeyModifiers.Windows,
            _ => VirtualKeyModifiers.None
        };
        return flag != VirtualKeyModifiers.None;
    }

    internal static bool TryParseGesture(string? text, out VirtualKey key, out VirtualKeyModifiers modifiers)
    {
        key = default;
        modifiers = VirtualKeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts[..^1])
        {
            if (!TryParseModifier(part, out var flag) || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var name = parts[^1];
        if (name.Length == 1 && char.IsAsciiLetter(name[0])) key = VirtualKey.A + (char.ToUpperInvariant(name[0]) - 'A');
        else if (name.Length == 1 && char.IsAsciiDigit(name[0])) key = VirtualKey.Number0 + (name[0] - '0');
        else if (name.Equals("Backspace", StringComparison.OrdinalIgnoreCase)) key = VirtualKey.Back;
        else if (name.Equals("Esc", StringComparison.OrdinalIgnoreCase)) key = VirtualKey.Escape;
        else if (name.Equals("Plus", StringComparison.OrdinalIgnoreCase)) key = (VirtualKey)187;
        else if (name.Equals("Minus", StringComparison.OrdinalIgnoreCase)) key = (VirtualKey)189;
        else if (!Enum.TryParse(name, ignoreCase: true, out key) ||
            !string.Equals(name, GetSingleKeyText(key), StringComparison.OrdinalIgnoreCase)) return false;
        return GetSingleKeyText(key) is not null;
    }

    public static string? CreateGestureText(VirtualKey key) => CreateGestureText(key, ReadModifiers());

    internal static string? CreateGestureText(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        var text = GetSingleKeyText(key);
        if (text is null) return null;
        return ((modifiers & VirtualKeyModifiers.Control) != 0 ? "Ctrl+" : "") +
            ((modifiers & VirtualKeyModifiers.Menu) != 0 ? "Alt+" : "") +
            ((modifiers & VirtualKeyModifiers.Shift) != 0 ? "Shift+" : "") +
            ((modifiers & VirtualKeyModifiers.Windows) != 0 ? "Win+" : "") + text;
    }

    internal static string? CreateText(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (ShortcutReservations.Find(CreateGestureText(key, modifiers)) is not null) return null;
        if (modifiers == VirtualKeyModifiers.None)
        {
            var text = GetSingleKeyText(key);
            return text is not null && TryNormalizeSingleKey(text, out var normalized) ? normalized : null;
        }

        if ((modifiers & (VirtualKeyModifiers.Shift | VirtualKeyModifiers.Windows)) != 0)
        {
            return null;
        }

        var control = (modifiers & VirtualKeyModifiers.Control) != 0;
        var alt = (modifiers & VirtualKeyModifiers.Menu) != 0;
        if (control == alt)
        {
            return null;
        }

        var digit = key switch
        {
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => (char)('0' + (key - VirtualKey.Number0)),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => (char)('0' + (key - VirtualKey.NumberPad0)),
            _ => '\0'
        };
        if (digit != '\0')
        {
            return $"{(control ? "Ctrl" : "Alt")}+{digit}";
        }

        return alt && key is >= VirtualKey.A and <= VirtualKey.Z
            ? $"Alt+{(char)('A' + (key - VirtualKey.A))}"
            : null;
    }

    internal static string? GetSingleKeyText(VirtualKey key) => key switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('A' + (key - VirtualKey.A))).ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (key - VirtualKey.Number0))).ToString(),
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => ((char)('0' + (key - VirtualKey.NumberPad0))).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F24 => $"F{1 + (key - VirtualKey.F1)}",
        VirtualKey.Back => "Backspace",
        (VirtualKey)187 => "Plus",
        (VirtualKey)189 => "Minus",
        VirtualKey.Escape or VirtualKey.Enter or VirtualKey.Tab or VirtualKey.Space or VirtualKey.Insert or VirtualKey.Delete
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown
            or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down => key.ToString(),
        _ => null
    };

    internal static bool TryNormalizeSingleKey(string text, out string? normalized)
    {
        normalized = null;
        if (ShortcutReservations.Find(text) is not null) return false;
        if (text.Length == 1 && char.IsAsciiLetterOrDigit(text[0]))
        {
            normalized = text.ToUpperInvariant();
            return true;
        }
        if (string.Equals(text, "Backspace", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Backspace";
            return true;
        }
        if (!Enum.TryParse<VirtualKey>(text, ignoreCase: true, out var key)) return false;
        var keyText = GetSingleKeyText(key);
        if (!string.Equals(text, keyText, StringComparison.OrdinalIgnoreCase)) return false;
        normalized = keyText;
        return normalized is not null;
    }

    private static bool IsDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
}
