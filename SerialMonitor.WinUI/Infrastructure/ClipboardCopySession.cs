namespace SerialMonitor.WinUI.Infrastructure;

// Used on the dialog's UI thread. A canceled request may still be unwinding
// when the user starts another copy; its completion must not clear the new one.
internal sealed class ClipboardCopySession
{
    private Request? _active;

    public Request? TryBegin(string preview)
    {
        if (_active?.Preview == preview) return null;
        Cancel();
        return _active = new Request(preview);
    }

    public void PreviewChanged(string preview)
    {
        if (_active is not null && _active.Preview != preview) Cancel();
    }

    public bool IsCurrent(Request request) =>
        ReferenceEquals(_active, request) && !request.Token.IsCancellationRequested;

    public void Complete(Request request)
    {
        if (ReferenceEquals(_active, request)) _active = null;
    }

    public void Cancel()
    {
        var request = _active;
        _active = null;
        request?.Cancel();
    }

    internal sealed class Request(string preview) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        public string Preview { get; } = preview;
        public CancellationToken Token => _cancellation.Token;
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose() => _cancellation.Dispose();
    }
}
