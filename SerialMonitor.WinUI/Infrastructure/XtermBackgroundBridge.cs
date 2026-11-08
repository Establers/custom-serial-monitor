using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SerialMonitor.WinUI.Infrastructure;

// Register both fixed roots BEFORE navigation. WebView2's resource loaders do
// not reliably observe virtual-host mappings added/changed on an open page.
public sealed class XtermBackgroundBridge
{
    private const string ImageHost = "serialmonitor-background.local";
    private const string BundledImageHost = "serialmonitor-background-bundled.local";
    private CoreWebView2? _owner;
    private string? _presetFolder;
    private string? _bundledFolder;
    private string? _imagePath;
    private long _imageRevision = -1;
    private long _sourceVersion;
    private string _imageUrl = string.Empty;
    private string? _lastPayload;

    public void Reset() => _lastPayload = null;

    public async Task InitializeAsync(CoreWebView2 webView, string profilePath, string bundledFolder,
        CancellationToken cancellationToken)
    {
        var presetFolder = Path.GetFullPath(profilePath) + ".backgrounds";
        // The root must exist even when the first custom image is imported later.
        await Task.Run(() => Directory.CreateDirectory(presetFolder), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        webView.SetVirtualHostNameToFolderMapping(ImageHost, presetFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        var bundledRoot = Path.GetFullPath(bundledFolder);
        if (Directory.Exists(bundledRoot))
            webView.SetVirtualHostNameToFolderMapping(BundledImageHost, bundledRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        _owner = webView;
        _presetFolder = presetFolder;
        _bundledFolder = bundledRoot;
        _imagePath = null;
        _lastPayload = null;
    }

    internal static string CreateImageUrl(string imagePath, string presetFolder, string bundledFolder, long version)
    {
        if (string.IsNullOrEmpty(imagePath)) return string.Empty;
        var fullPath = Path.GetFullPath(imagePath);
        var folder = Path.GetDirectoryName(fullPath);
        var host = string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(presetFolder)), StringComparison.OrdinalIgnoreCase) ? ImageHost
            : string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundledFolder)), StringComparison.OrdinalIgnoreCase) ? BundledImageHost
            : throw new InvalidOperationException("The xterm background must be a stored preset or bundled image. Choose the image again to save a preset copy.");
        return $"https://{host}/{Uri.EscapeDataString(Path.GetFileName(fullPath))}?v={version}";
    }

    public async Task ApplyAsync(CoreWebView2 webView, string imagePath, double opacity,
        long imageRevision, XtermBackgroundLayout? layout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_owner, webView))
            throw new InvalidOperationException("The xterm image roots must be initialized before navigation.");

        if (!string.Equals(_imagePath, imagePath, StringComparison.Ordinal) || _imageRevision != imageRevision)
        {
            // Only the URL/version changes; the registered folders stay fixed.
            _imageUrl = CreateImageUrl(imagePath, _presetFolder!, _bundledFolder!, ++_sourceVersion);
            _imagePath = imagePath;
            _imageRevision = imageRevision;
        }

        var payload = JsonSerializer.Serialize(new { source = _imageUrl, opacity, layout });
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        try
        {
            var result = await webView.ExecuteScriptAsync(
                $"window.serialMonitorSetBackground && window.serialMonitorSetBackground({payload});").AsTask(cancellationToken);
            if (result != "true") throw new InvalidOperationException("xterm background update was rejected.");
        }
        catch
        {
            if (_lastPayload == payload) _lastPayload = null;
            throw;
        }
    }
}
