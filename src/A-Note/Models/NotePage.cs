namespace ANote.Models;

public enum PaperStyle { Blank, Ruled, Grid, Dotted }

public sealed class NotePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NotebookId { get; set; } = "";
    public int SortOrder { get; set; }
    public PaperStyle PaperStyle { get; set; } = PaperStyle.Ruled;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.Now;
    public List<InkStrokeData> Strokes { get; set; } = [];
    public List<PagePhotoData> Photos { get; set; } = [];
}
