using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ANote.Storage;

namespace ANote.Services;

public static class CrashLogger
{
    private static readonly object Gate = new();
    private const long MaximumLogBytes = 4 * 1024 * 1024;
    public static string LogPath => Path.Combine(AppPaths.CrashLogDirectory, "crashes.log");

    public static void Initialize()
    {
        AppPaths.EnsureCreated();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("AppDomain.UnhandledException", args.ExceptionObject as Exception,
                $"terminating={args.IsTerminating}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
        Write("Application.Start", null,
            $"version={Assembly.GetExecutingAssembly().GetName().Version}; os={Environment.OSVersion}; arch={RuntimeInformation.ProcessArchitecture}");
    }

    public static void Write(string source, Exception? exception, string? details = null)
    {
        try
        {
            AppPaths.EnsureCreated();
            var entry = new StringBuilder()
                .AppendLine("--------------------------------------------------------------------------------")
                .Append(DateTimeOffset.Now.ToString("O")).Append(" | ").Append(source)
                .Append(" | process=").Append(Environment.ProcessId)
                .Append(" | thread=").Append(Environment.CurrentManagedThreadId).AppendLine();
            if (!string.IsNullOrWhiteSpace(details)) entry.AppendLine(details);
            if (exception is not null) entry.AppendLine(exception.ToString());

            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.CrashLogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaximumLogBytes)
                {
                    var previous = Path.Combine(AppPaths.CrashLogDirectory, "crashes.previous.log");
                    File.Move(LogPath, previous, overwrite: true);
                }
                File.AppendAllText(LogPath, entry.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become another application failure.
        }
    }
}
