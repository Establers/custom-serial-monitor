using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialMonitor.WinUI.Infrastructure;
using SerialMonitor.WinUI.ViewModels;
using Windows.ApplicationModel.DataTransfer;

namespace SerialMonitor.WinUI.Views;

public sealed partial class PluginGuideView : UserControl
{
    private readonly PluginGuideViewModel _model = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _feedbackTimer;

    public PluginGuideView()
    {
        InitializeComponent();
        GuideRoot.DataContext = _model;
        Unloaded += (_, _) => _feedbackTimer?.Stop();
    }

    private void CopySnippet_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string text }) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            _model.ActionStatus = "복사했습니다. AI 도구나 설정 파일에 붙여넣으세요.";
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("PluginGuide.Copy", ex);
            _model.ActionStatus = "복사하지 못했습니다. 본문을 선택해 Ctrl+C로 복사하세요.";
        }
        CopyFeedback.Visibility = Visibility.Visible;
        if (_feedbackTimer is null)
        {
            _feedbackTimer = DispatcherQueue.CreateTimer();
            _feedbackTimer.Interval = TimeSpan.FromSeconds(4);
            _feedbackTimer.IsRepeating = false;
            _feedbackTimer.Tick += (_, _) => CopyFeedback.Visibility = Visibility.Collapsed;
        }
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void FindTool_Click(object sender, RoutedEventArgs args) => _model.RefreshToolPath();

    private void Page_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        ArticleScroll?.ChangeView(null, 0, null, true);
        if (CopyFeedback is not null) CopyFeedback.Visibility = Visibility.Collapsed;
    }

    private void Client_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CopyFeedback is not null) CopyFeedback.Visibility = Visibility.Collapsed;
    }

    private void Guide_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 680;
        NavigationColumn.Width = new GridLength(compact ? 0 : 140);
        WideNavigation.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        var stackFields = args.NewSize.Width < 800;
        ClientColumn.Width = stackFields ? new GridLength(1, GridUnitType.Star) : new GridLength(200);
        Grid.SetColumnSpan(ClientField, stackFields ? 2 : 1);
        Grid.SetColumn(ToolPathField, stackFields ? 0 : 1);
        Grid.SetRow(ToolPathField, stackFields ? 1 : 0);
        Grid.SetColumnSpan(ToolPathField, stackFields ? 2 : 1);
    }
}
