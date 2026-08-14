using Microsoft.UI.Xaml;
using SerialMonitor.WinUI.Infrastructure;

namespace SerialMonitor.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        RuntimeDiagnostics.StartGeneralDiagnosticSession();
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
            RuntimeDiagnostics.RecordFatalError("App.OnLaunched", ex);
            throw;
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        RuntimeDiagnostics.RecordFatalError("Application.UnhandledException", args.Exception);
    }

    private static void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            RuntimeDiagnostics.RecordFatalError("AppDomain.UnhandledException", exception);
        }
        else
        {
            RuntimeDiagnostics.RecordFatalError(
                "AppDomain.UnhandledException",
                new InvalidOperationException("The runtime supplied a non-Exception fatal object."));
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        RuntimeDiagnostics.RecordError("TaskScheduler.UnobservedTaskException", args.Exception);
    }
}
