using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace SerialMonitor.WinUI.Infrastructure;

internal static class UiText
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader());

    public static string Get(string resourceId, string fallback)
    {
        try
        {
            var value = Loader.Value.GetString(resourceId);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError($"UiText.Get.{resourceId}", ex);
            return fallback;
        }
    }

    public static string Format(string resourceId, string fallbackFormat, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(resourceId, fallbackFormat), args);
    }
}
