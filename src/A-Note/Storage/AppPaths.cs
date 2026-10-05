namespace ANote.Storage;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A-Note");
    public static string DatabasePath => Path.Combine(Root, "a-note.db");
    public static string InkDirectory => Path.Combine(Root, "ink");
    public static string AttachmentDirectory => Path.Combine(Root, "attachments");
    public static string DatabaseRecoveryDirectory => Path.Combine(Root, "database-recovery");
    public static string CrashLogDirectory => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InkDirectory);
        Directory.CreateDirectory(AttachmentDirectory);
        Directory.CreateDirectory(DatabaseRecoveryDirectory);
        Directory.CreateDirectory(CrashLogDirectory);
    }

    public static string InkPath(string pageId) => Path.Combine(InkDirectory, pageId + ".json");
}
