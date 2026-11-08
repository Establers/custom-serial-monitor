using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace SerialMonitor.WinUI.Infrastructure;

// UI-thread bridge: map local images without embedding image bytes in scripts.
// A new URL is needed when a preset replaces the file at the same path.
public sealed class XtermBackgroundBridge
{
    private const string ImageHost = "serialmonitor-background.local";
    private CoreWebView2? _owner;
    private string? _mappedFolder;
    private string? _imagePath;
    private long _imageRevision = -1;
    private long _sourceVersion;
    private string _imageUrl = string.Empty;
    private string? _lastPayload;

    public void Reset() => _lastPayload = null;

    public async Task ApplyAsync(CoreWebView2 webView, string imagePath, double opacity,
        long imageRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_owner, webView))
        {
            _owner = webView;
            _mappedFolder = null;
            _imagePath = null;
            _lastPayload = null;
        }

        if (!string.Equals(_imagePath, imagePath, StringComparison.Ordinal) || _imageRevision != imageRevision)
        {
            var folder = string.IsNullOrEmpty(imagePath) ? null : Path.GetDirectoryName(Path.GetFullPath(imagePath));
            if (!string.Equals(_mappedFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                if (_mappedFolder is not null) webView.ClearVirtualHostNameToFolderMapping(ImageHost);
                _mappedFolder = null;
                if (folder is not null)
                {
                    // Permit image display while denying cross-origin fetch/XHR access.
                    webView.SetVirtualHostNameToFolderMapping(ImageHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
                    _mappedFolder = folder;
                }
            }

            _imageUrl = folder is null ? string.Empty
                : $"https://{ImageHost}/{Uri.EscapeDataString(Path.GetFileName(imagePath))}?v={++_sourceVersion}";
            _imagePath = imagePath;
            _imageRevision = imageRevision;
        }

        var payload = JsonSerializer.Serialize(new { source = _imageUrl, opacity });
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
