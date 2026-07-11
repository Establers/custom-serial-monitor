using System.Collections.ObjectModel;
using System.Globalization;
using SerialMonitor.WinUI.Models;

namespace SerialMonitor.WinUI.ViewModels;

public sealed class CommandViewModel : ViewModelBase
{
    public const int DefaultMaxHistoryCount = 100;

    private string _currentCommandText = string.Empty;
    private int _historyCursor = -1;
    private string _historyDraft = string.Empty;
    private bool _isApplyingHistory;
    private string _lastHistoryCommand = string.Empty;
    private DateTimeOffset? _lastHistoryUpdateTime;
    private long _historyErrorCount;
    private string _lastHistoryError = string.Empty;

    public ObservableCollection<TxCommand> SavedCommands { get; } = new();

    public ObservableCollection<CommandHistoryEntry> CommandHistory { get; } = new();

    public string CurrentCommandText
    {
        get => _currentCommandText;
        set
        {
            if (SetProperty(ref _currentCommandText, value) && !_isApplyingHistory && _historyCursor >= 0)
            {
                _historyCursor = -1;
            }
        }
    }

    public int HistoryMaxCount => DefaultMaxHistoryCount;

    public int CommandHistoryCount => CommandHistory.Count;

    public string LastHistoryCommand => _lastHistoryCommand;

    public string LastHistoryUpdateTimeText => _lastHistoryUpdateTime?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "(none)";

    public long HistoryErrorCount => Interlocked.Read(ref _historyErrorCount);

    public string LastHistoryError => _lastHistoryError;

    public void AddToHistory(string commandText, DateTimeOffset? sentAt = null)
    {
        var normalized = NormalizeCommandText(commandText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        try
        {
            var timestamp = sentAt ?? DateTimeOffset.Now;
            var existing = CommandHistory.FirstOrDefault(
                entry => string.Equals(entry.CommandText, normalized, StringComparison.Ordinal));

            if (existing is not null)
            {
                CommandHistory.Remove(existing);
                existing.Count = Math.Max(1, existing.Count) + 1;
                existing.LastSentTime = timestamp;
                CommandHistory.Insert(0, existing);
            }
            else
            {
                CommandHistory.Insert(0, new CommandHistoryEntry
                {
                    CommandText = normalized,
                    LastSentTime = timestamp,
                    Count = 1
                });
            }

            while (CommandHistory.Count > DefaultMaxHistoryCount)
            {
                CommandHistory.RemoveAt(CommandHistory.Count - 1);
            }

            _historyCursor = -1;
            _historyDraft = string.Empty;
            _lastHistoryCommand = normalized;
            _lastHistoryUpdateTime = timestamp;
            _lastHistoryError = string.Empty;
            NotifyHistoryPropertiesChanged();
        }
        catch (Exception ex)
        {
            RecordHistoryError($"Command history update failed: {ex.Message}");
        }
    }

    public bool NavigateHistory(int direction)
    {
        if (CommandHistory.Count == 0)
        {
            return false;
        }

        try
        {
            if (direction < 0)
            {
                if (_historyCursor < 0)
                {
                    _historyDraft = CurrentCommandText;
                    _historyCursor = 0;
                }
                else
                {
                    _historyCursor = Math.Min(CommandHistory.Count - 1, _historyCursor + 1);
                }
            }
            else if (direction > 0)
            {
                if (_historyCursor < 0)
                {
                    return false;
                }

                _historyCursor--;
                if (_historyCursor < 0)
                {
                    SetCurrentCommandFromHistory(_historyDraft);
                    _historyDraft = string.Empty;
                    return true;
                }
            }
            else
            {
                return false;
            }

            SetCurrentCommandFromHistory(CommandHistory[_historyCursor].CommandText);
            return true;
        }
        catch (Exception ex)
        {
            RecordHistoryError($"Command history navigation failed: {ex.Message}");
            return false;
        }
    }

    public void SelectHistoryEntry(CommandHistoryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        SetCurrentCommandFromHistory(entry.CommandText);
        _historyCursor = -1;
        _historyDraft = string.Empty;
    }

    public void ClearHistory()
    {
        CommandHistory.Clear();
        _historyCursor = -1;
        _historyDraft = string.Empty;
        _lastHistoryCommand = string.Empty;
        _lastHistoryUpdateTime = null;
        _lastHistoryError = string.Empty;
        NotifyHistoryPropertiesChanged();
    }

    public IReadOnlyList<CommandHistoryEntry> GetHistorySnapshot()
    {
        return CommandHistory
            .Select(CloneHistoryEntry)
            .ToArray();
    }

    public void LoadHistory(IEnumerable<CommandHistoryEntry>? history)
    {
        CommandHistory.Clear();
        if (history is not null)
        {
            foreach (var entry in history
                         .Select(CloneHistoryEntry)
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.CommandText))
                         .OrderByDescending(entry => entry.LastSentTime)
                         .Take(DefaultMaxHistoryCount))
            {
                CommandHistory.Add(entry);
            }
        }

        _historyCursor = -1;
        _historyDraft = string.Empty;
        var newest = CommandHistory.FirstOrDefault();
        _lastHistoryCommand = newest?.CommandText ?? string.Empty;
        _lastHistoryUpdateTime = newest?.LastSentTime;
        _lastHistoryError = string.Empty;
        NotifyHistoryPropertiesChanged();
    }

    private void SetCurrentCommandFromHistory(string commandText)
    {
        _isApplyingHistory = true;
        try
        {
            CurrentCommandText = commandText;
        }
        finally
        {
            _isApplyingHistory = false;
        }
    }

    private void RecordHistoryError(string message)
    {
        Interlocked.Increment(ref _historyErrorCount);
        _lastHistoryError = string.IsNullOrWhiteSpace(message)
            ? "Command history error."
            : message.Trim();
        OnPropertyChanged(nameof(HistoryErrorCount));
        OnPropertyChanged(nameof(LastHistoryError));
    }

    private void NotifyHistoryPropertiesChanged()
    {
        OnPropertyChanged(nameof(CommandHistoryCount));
        OnPropertyChanged(nameof(LastHistoryCommand));
        OnPropertyChanged(nameof(LastHistoryUpdateTimeText));
        OnPropertyChanged(nameof(LastHistoryError));
    }

    private static CommandHistoryEntry CloneHistoryEntry(CommandHistoryEntry entry)
    {
        return new CommandHistoryEntry
        {
            CommandText = NormalizeCommandText(entry.CommandText),
            LastSentTime = entry.LastSentTime == default ? DateTimeOffset.Now : entry.LastSentTime,
            Count = Math.Max(1, entry.Count)
        };
    }

    private static string NormalizeCommandText(string? commandText)
    {
        return commandText?.Trim() ?? string.Empty;
    }
}
