using Microsoft.UI.Xaml;

namespace ANote;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                Storage.AppPaths.EnsureCreated();
                File.WriteAllText(Path.Combine(Storage.AppPaths.Root, "last-crash.txt"), e.Exception.ToString());
            }
            catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
