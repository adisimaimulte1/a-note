namespace ANote.Storage;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "A-Note");
    public static string DatabasePath => Path.Combine(Root, "a-note.db");
    public static string InkDirectory => Path.Combine(Root, "ink");
    public static string AttachmentDirectory => Path.Combine(Root, "attachments");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(InkDirectory);
        Directory.CreateDirectory(AttachmentDirectory);
    }

    public static string InkPath(string pageId) => Path.Combine(InkDirectory, pageId + ".json");
}
