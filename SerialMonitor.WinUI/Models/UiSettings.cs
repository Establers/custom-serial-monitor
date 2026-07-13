namespace SerialMonitor.WinUI.Models;

public enum MockGeneratorPattern
{
    NormalLines,
    NoNewlineZzz,
    NoNewlineZzzBurst
}

public sealed class UiSettings
{
    public int MaxVisibleLogLines { get; set; } = 50_000;

    public int MaxVisibleEventCount { get; set; } = 1_000;

    public bool AutoScrollEnabled { get; set; } = true;

    public bool EventAutoScrollEnabled { get; set; } = true;

    public int XtermScrollbackSize { get; set; } = 50_000;

    public bool ConfirmBeforeDisconnect { get; set; } = true;

    public bool ShowTimestampInLogView { get; set; } = true;

    public bool ApplyRulesToNewLogsOnly { get; set; } = true;

    public RxDisplayMode RxDisplayMode { get; set; } = RxDisplayMode.Terminal;

    public int HexGroupTimeoutMs { get; set; } = 100;

    public TxSendMode TxSendMode { get; set; } = TxSendMode.Terminal;

    public bool CuteBackgroundMode { get; set; }

    public string CuteBackgroundImagePath { get; set; } = string.Empty;

    public double CuteBackgroundOpacity { get; set; } = 0.25;

    public bool SearchCaseSensitive { get; set; }

    public bool SearchResultAutoRefreshEnabled { get; set; }

    public string LastSearchText { get; set; } = string.Empty;

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
            MaxVisibleLogLines = MaxVisibleLogLines,
            MaxVisibleEventCount = MaxVisibleEventCount,
            AutoScrollEnabled = AutoScrollEnabled,
            EventAutoScrollEnabled = EventAutoScrollEnabled,
            XtermScrollbackSize = XtermScrollbackSize,
            ConfirmBeforeDisconnect = ConfirmBeforeDisconnect,
            ShowTimestampInLogView = ShowTimestampInLogView,
            ApplyRulesToNewLogsOnly = ApplyRulesToNewLogsOnly,
            RxDisplayMode = RxDisplayMode,
            HexGroupTimeoutMs = HexGroupTimeoutMs,
            TxSendMode = TxSendMode,
            CuteBackgroundMode = CuteBackgroundMode,
            CuteBackgroundImagePath = CuteBackgroundImagePath,
            CuteBackgroundOpacity = CuteBackgroundOpacity,
            SearchCaseSensitive = SearchCaseSensitive,
            SearchResultAutoRefreshEnabled = SearchResultAutoRefreshEnabled,
            LastSearchText = LastSearchText,
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
