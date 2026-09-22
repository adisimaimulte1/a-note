namespace ANote.Models;

public sealed class InkStrokeData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tool { get; set; } = "pen";
    public string Color { get; set; } = "#171717";
    public double Width { get; set; } = 2.2;
    public List<InkPointData> Points { get; set; } = [];
}

public sealed class InkPointData
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Pressure { get; set; } = 0.5f;
}
