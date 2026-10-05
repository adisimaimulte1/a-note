using Microsoft.UI.Xaml;
using ANote.Services;

namespace ANote;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        CrashLogger.Initialize();
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            CrashLogger.Write("WinUI.Application.UnhandledException", e.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
