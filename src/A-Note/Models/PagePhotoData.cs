namespace ANote.Models;

public sealed class PagePhotoData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = "";
    public double X { get; set; } = 180;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 280;
    public double Rotation { get; set; }
}
