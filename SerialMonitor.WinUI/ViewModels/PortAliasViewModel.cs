using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.Services;

namespace SerialMonitor.WinUI.ViewModels;

// Window naming has no dependency on serial connection, file logging or profiles.
public sealed class PortAliasViewModel : ViewModelBase, IDisposable
{
    private readonly IPortAliasService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private Task? _initialization;
    private string _port = string.Empty;
    private string _editingPort = string.Empty;
    private string _draftAlias = string.Empty;
    private string _error = string.Empty;
    private bool _ready;
    private bool _saving;
    private bool _disposed;

    public PortAliasViewModel(IPortAliasService? service = null) => _service = service ?? new PortAliasService();

    public string PortName => _port;
    public string Alias => _aliases.GetValueOrDefault(_port, string.Empty);
    public string ButtonText => Alias.Length == 0 ? UiText.Get("PortAliasAddName", "Add name") : Alias;
    public string WindowTitle => _port.Length == 0 ? "Serial Monitor"
        : Alias.Length == 0 ? $"{_port} - Serial Monitor" : $"{Alias} · {_port} - Serial Monitor";
    public bool CanEdit => _ready && !_saving && !_disposed && _port.Length > 0;
    public bool CanApply => CanEdit && _editingPort == _port;
    public string EditingPortText => UiText.Format("PortAliasEditorTitle", "Name for {0}", _editingPort);
    public string ErrorMessage => _error;
    public string ErrorVisibility => _error.Length == 0 ? "Collapsed" : "Visible";
    public string ToolTip => _port.Length == 0
        ? UiText.Get("PortAliasSelectPort", "Select a port to name this window.")
        : UiText.Format("PortAliasToolTip", "Edit the name for {0}. Current name: {1}", _port,
            Alias.Length == 0 ? UiText.Get("PortAliasNoName", "none") : Alias);

    public string DraftAlias
    {
        get => _draftAlias;
        set => SetProperty(ref _draftAlias, value ?? string.Empty);
    }

    public void SetPort(string? portName)
    {
        if (_disposed) return;
        var port = string.Empty;
        // A bad port restored from an externally edited profile must not make
        // the naming feature throw from a UI property-change callback.
        try
        {
            if (!string.IsNullOrWhiteSpace(portName)) port = PortAliasService.NormalizePortName(portName);
        }
        catch (ArgumentException) { }
        if (_port == port) return;
        _port = port;
        EndEdit();
        Refresh();
    }

    public Task InitializeAsync() => _disposed ? Task.CompletedTask : _initialization ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var saved = await _service.LoadAsync(_shutdown.Token);
            if (_disposed) return;
            foreach (var entry in saved) _aliases[entry.Key] = entry.Value;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (!_disposed) SetError(UiText.Get("PortAliasLoadFailed", "Saved names could not be loaded."), ex);
        }
        if (_disposed) return;
        _ready = true;
        Refresh();
    }

    public void BeginEdit()
    {
        if (!CanEdit) return;
        _editingPort = _port;
        DraftAlias = Alias;
        OnPropertyChanged(nameof(EditingPortText));
        OnPropertyChanged(nameof(CanApply));
    }

    public void EndEdit()
    {
        if (_disposed) return;
        _editingPort = string.Empty;
        DraftAlias = string.Empty;
        OnPropertyChanged(nameof(CanApply));
    }

    public async Task<bool> ApplyAsync()
    {
        if (!CanApply) return false;
        var port = _editingPort;
        string alias;
        try { alias = PortAliasService.NormalizeAlias(DraftAlias); }
        catch (ArgumentException)
        {
            _error = UiText.Get("PortAliasInvalidName", "Use at most 64 valid characters on one line; control marks are not allowed.");
            RefreshError();
            return false;
        }

        _saving = true;
        Refresh();
        try
        {
            // Capture the port before awaiting. Selection may change during disk I/O.
            await _service.SetAsync(port, alias, _shutdown.Token);
            if (_disposed) return false;
            if (alias.Length == 0) _aliases.Remove(port);
            else _aliases[port] = alias;
            _error = string.Empty;
            RefreshError();
            return _port == port && _editingPort == port;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!_disposed) SetError(UiText.Get("PortAliasSaveFailed", "Name could not be saved. Try again."), ex);
            return false;
        }
        finally
        {
            _saving = false;
            if (!_disposed) Refresh();
        }
    }

    private void SetError(string message, Exception exception)
    {
        _error = message;
        RuntimeDiagnostics.RecordError("PortAlias", exception);
        RefreshError();
    }

    private void RefreshError()
    {
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ErrorVisibility));
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(PortName), nameof(Alias), nameof(ButtonText),
            nameof(WindowTitle), nameof(ToolTip), nameof(CanEdit), nameof(CanApply) })
            OnPropertyChanged(property);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
