namespace ANote.Models;

public sealed class Notebook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled notebook";
    public string? Subject { get; set; }
    public string Accent { get; set; } = "#FF7A18";
    public bool IsFavorite { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public int PageCount { get; set; }
    // UI-only measurement. MainWindow assigns this from the stable list viewport before
    // an item enters the collection, so recycled templates never render at a default height.
    public double CardHeight { get; set; }
    public string PageCountLabel => PageCount == 1 ? "1 page" : $"{PageCount} pages";
    public string ModifiedLabel => $"Updated {ModifiedAt.LocalDateTime:dd MMM yyyy}";
}
