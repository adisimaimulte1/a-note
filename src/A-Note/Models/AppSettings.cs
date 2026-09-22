namespace ANote.Models;

public sealed class AppSettings
{
    public int X { get; set; } = 120;
    public int Y { get; set; } = 80;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 850;
    public bool Maximized { get; set; }
    public string? LastNotebookId { get; set; }
    public double Zoom { get; set; } = 1.0;
    public bool MouseDrawingEnabled { get; set; }
}
