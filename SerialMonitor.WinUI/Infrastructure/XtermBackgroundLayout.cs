using System.Text.Json.Serialization;

namespace SerialMonitor.WinUI.Infrastructure;

// Image bounds in units of the native WebView viewport. Percentages preserve
// the same crop at different DPI settings and browser/device pixel ratios.
public readonly record struct XtermBackgroundLayout(
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("top")] double Top,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);
