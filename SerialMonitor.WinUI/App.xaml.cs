using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        RuntimeDiagnostics.RecordStartup();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            RuntimeDiagnostics.RecordError("App.OnLaunched", ex);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        RuntimeDiagnostics.RecordError("Application.UnhandledException", args.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            RuntimeDiagnostics.RecordError("AppDomain.UnhandledException", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        RuntimeDiagnostics.RecordError("TaskScheduler.UnobservedTaskException", args.Exception);
    }
}
