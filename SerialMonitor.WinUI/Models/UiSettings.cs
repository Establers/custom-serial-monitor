namespace SerialMonitor.WinUI.Models;

public enum AppTheme
{
    System,
    Dark,
    Light
}

public enum MockGeneratorPattern
{
    NormalLines,
    NoNewlineZzz,
    NoNewlineZzzBurst,
    VisualHexPackets
}

public enum TimestampDisplayFormat
{
    DateTimeMilliseconds,
    DateTimeSeconds,
    TimeMilliseconds,
    TimeSeconds,
    MonthDayTimeMilliseconds,
    MonthDayTimeSeconds,
    ShortDateTimeMilliseconds,
    ShortDateTimeSeconds,
    SlashDateTimeMilliseconds,
    SlashDateTimeSeconds,
    SlashMonthDayTimeMilliseconds,
    SlashMonthDayTimeSeconds
}

public enum XtermFontFamily
{
    JetBrainsMono,
    CascadiaMono,
    Consolas,
    D2Coding,
    CourierNew,
    LucidaConsole
}

public sealed class UiSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public const int DefaultVisibleLogLines = 1_000_000;
    public const int MaxVisibleLogLinesLimit = 1_000_000;
    public const int DefaultLogMemoryBudgetMiB = 256;
    public const int MinLogMemoryBudgetMiB = 64;
    public const int MaxLogMemoryBudgetMiB = 2048;
    public const int FixedMaxVisibleEventCount = 100;
    public const int DefaultHexGroupTimeoutMs = SerialMonitor.Core.SerialTimingAdvisor.MinimumSuggestedTimeoutMilliseconds;
    public const int DefaultXtermFontSize = 13;
    public const int MinXtermFontSize = 10;
    public const int MaxXtermFontSize = 15;

    public int MaxVisibleLogLines { get; set; } = DefaultVisibleLogLines;

    public int LogMemoryBudgetMiB { get; set; } = DefaultLogMemoryBudgetMiB;

    public int MaxVisibleEventCount { get; set; } = FixedMaxVisibleEventCount;

    public bool AutoScrollEnabled { get; set; } = true;

    public bool FileLoggingWhileViewPaused { get; set; } = true;

    public bool EventAutoScrollEnabled { get; set; } = true;

    public bool ConfirmBeforeDisconnect { get; set; } = true;

    public bool AutoReconnectEnabled { get; set; } = true;

    public bool ShowTimestampInLogView { get; set; } = true;

    public bool ShowRxTxDirectionPrefixInLogView { get; set; } = true;

    public TimestampDisplayFormat TimestampDisplayFormat { get; set; } = TimestampDisplayFormat.DateTimeMilliseconds;

    public XtermFontFamily XtermFontFamily { get; set; } = XtermFontFamily.Consolas;

    public int XtermFontSize { get; set; } = DefaultXtermFontSize;

    public RxDisplayMode RxDisplayMode { get; set; } = RxDisplayMode.Terminal;

    public int HexGroupTimeoutMs { get; set; } = DefaultHexGroupTimeoutMs;

    public bool HexGroupTimeoutUserConfigured { get; set; }

    public TxSendMode TxSendMode { get; set; } = TxSendMode.Terminal;

    public bool CuteBackgroundMode { get; set; }

    public string CuteBackgroundImagePath { get; set; } = string.Empty;

    public double CuteBackgroundOpacity { get; set; } = 0.25;

    public double XtermBackgroundOpacity { get; set; } = BackgroundPreset.DefaultXtermOpacity;

    public const int BackgroundPresetCount = 5;

    public int SelectedBackgroundPreset { get; set; } = 1;

    // Null identifies profiles written before presets existed.
    public List<BackgroundPreset>? BackgroundPresets { get; set; }

    public void EnsureBackgroundPresets()
    {
        if (BackgroundPresets is null || BackgroundPresets.Count == 0)
        {
            BackgroundPresets = [new() { ImagePath = CuteBackgroundImagePath, Opacity = CuteBackgroundOpacity, XtermOpacity = XtermBackgroundOpacity }];
            SelectedBackgroundPreset = 1;
        }

        BackgroundPresets = BackgroundPresets.Take(BackgroundPresetCount)
            .Select(preset => preset ?? new BackgroundPreset()).ToList();
        while (BackgroundPresets.Count < BackgroundPresetCount) BackgroundPresets.Add(new BackgroundPreset());
        if (SelectedBackgroundPreset is < 1 or > BackgroundPresetCount) SelectedBackgroundPreset = 1;
    }

    public void ApplySelectedBackgroundPreset()
    {
        EnsureBackgroundPresets();
        var preset = BackgroundPresets![SelectedBackgroundPreset - 1];
        CuteBackgroundImagePath = preset.ImagePath;
        CuteBackgroundOpacity = preset.Opacity;
        XtermBackgroundOpacity = preset.XtermOpacity;
    }

    public bool SearchCaseSensitive { get; set; }

    public bool SearchWholeWord { get; set; }

    public bool SearchUseRegularExpression { get; set; }

    public string LastSearchText { get; set; } = string.Empty;

    public List<string> SearchHistory { get; set; } = new();

    public string MarkerText { get; set; } = string.Empty;

    public bool ShowMockTestPort { get; set; } = DefaultShowMockTestPort;

    public int MockStressLinesPerSecond { get; set; } = 10;

    public int MockStressBurstSize { get; set; } = 1;

    public bool MockStressEventInjectionEnabled { get; set; } = true;

    public bool MockStressInvalidByteInjectionEnabled { get; set; }

    public MockGeneratorPattern MockGeneratorPattern { get; set; } = MockGeneratorPattern.NormalLines;

    public UiSettings Clone()
    {
        return new UiSettings
        {
            Theme = Theme,
            MaxVisibleLogLines = MaxVisibleLogLines,
            LogMemoryBudgetMiB = LogMemoryBudgetMiB,
            MaxVisibleEventCount = MaxVisibleEventCount,
            AutoScrollEnabled = AutoScrollEnabled,
            FileLoggingWhileViewPaused = FileLoggingWhileViewPaused,
            EventAutoScrollEnabled = EventAutoScrollEnabled,
            ConfirmBeforeDisconnect = ConfirmBeforeDisconnect,
            AutoReconnectEnabled = AutoReconnectEnabled,
            ShowTimestampInLogView = ShowTimestampInLogView,
            ShowRxTxDirectionPrefixInLogView = ShowRxTxDirectionPrefixInLogView,
            TimestampDisplayFormat = TimestampDisplayFormat,
            XtermFontFamily = XtermFontFamily,
            XtermFontSize = XtermFontSize,
            RxDisplayMode = RxDisplayMode,
            HexGroupTimeoutMs = HexGroupTimeoutMs,
            HexGroupTimeoutUserConfigured = HexGroupTimeoutUserConfigured,
            TxSendMode = TxSendMode,
            CuteBackgroundMode = CuteBackgroundMode,
            CuteBackgroundImagePath = CuteBackgroundImagePath,
            CuteBackgroundOpacity = CuteBackgroundOpacity,
            XtermBackgroundOpacity = XtermBackgroundOpacity,
            SelectedBackgroundPreset = SelectedBackgroundPreset,
            BackgroundPresets = BackgroundPresets?.Select(preset => preset?.Clone() ?? new BackgroundPreset()).ToList(),
            SearchCaseSensitive = SearchCaseSensitive,
            SearchWholeWord = SearchWholeWord,
            SearchUseRegularExpression = SearchUseRegularExpression,
            LastSearchText = LastSearchText,
            SearchHistory = SearchHistory?.ToList() ?? [],
            MarkerText = MarkerText,
            ShowMockTestPort = ShowMockTestPort,
            MockStressLinesPerSecond = MockStressLinesPerSecond,
            MockStressBurstSize = MockStressBurstSize,
            MockStressEventInjectionEnabled = MockStressEventInjectionEnabled,
            MockStressInvalidByteInjectionEnabled = MockStressInvalidByteInjectionEnabled,
            MockGeneratorPattern = MockGeneratorPattern
        };
    }

    private static bool DefaultShowMockTestPort
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
}
