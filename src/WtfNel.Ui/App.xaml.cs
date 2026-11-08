using System.IO;
using System.Windows;
using System.Windows.Threading;
using WtfNel.Core.Logging;
using WtfNel.Ui.Services;

namespace WtfNel.Ui;

/// <summary>Application entry: opens the log file, loads configuration and applies the saved theme before the main window appears.</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Log.ConsoleEnabled = false;
        LogBuffer.Attach();
        Log.Open(Path.Combine(AppContext.BaseDirectory, "logs", $"shadow-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        DispatcherUnhandledException += OnUnhandled;
        AppState.Load();
        ThemeService.Apply(AppState.Ui.Theme);
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppState.StopGame();
        Log.Close();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面异常：" + e.Exception.GetBaseException().Message);
        Log.Debug(e.Exception.ToString());
        e.Handled = true;
        _ = Dialogs.AlertAsync("出错了", e.Exception.GetBaseException().Message, DialogKind.Error);
    }
}
