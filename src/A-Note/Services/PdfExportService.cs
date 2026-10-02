using System.Text;
using System.Globalization;
using ANote.Ink;
using ANote.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ANote.Services;

public static class PdfExportService
{
    private const int ImageWidth = 1600;
    private const int ImageHeight = 900;
    private const double PdfWidth = 720;
    private const double PdfHeight = 405;

    public static async Task ExportAsync(
        string path,
        IReadOnlyList<NotePage> pages,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0) throw new InvalidOperationException("The notebook has no pages to export.");
        var images = new List<byte[]>(pages.Count);
        for (var index = 0; index < pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // BitmapEncoder/BitmapDecoder and WinRT random-access streams are COM-backed.
            // Keep that async imaging work on the caller's apartment instead of hopping through
            // Task.Run; doing so can terminate the unpackaged WinUI process with a COM failure.
            images.Add(await RenderJpegAsync(pages[index], cancellationToken));
            progress?.Report((index + 1) * 90 / pages.Count);
        }

        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The PDF destination folder is invalid.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await Task.Run(() =>
            {
                WritePdf(temporary, images, cancellationToken);
                ValidatePdf(temporary, images.Count);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: true);
            }, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        progress?.Report(100);
    }

    private static async Task<byte[]> RenderJpegAsync(NotePage page, CancellationToken cancellationToken)
    {
        var pixels = new byte[ImageWidth * ImageHeight * 4];
        Fill(pixels, 10, 10, 9, 255);
        var scaleX = ImageWidth / InkPageControl.PaperWidth;
        var scaleY = ImageHeight / InkPageControl.PaperHeight;
        DrawPaper(pixels, page.PaperStyle, scaleX, scaleY);

        foreach (var photo in page.Photos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CompositePhotoAsync(pixels, photo, scaleX, scaleY, cancellationToken);
        }
        // Highlights live below pen ink in the editor, regardless of when they were drawn.
        foreach (var stroke in page.Strokes.Where(stroke => stroke.Tool == "highlighter"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawStroke(pixels, stroke, scaleX, scaleY);
        }
        foreach (var stroke in page.Strokes.Where(stroke => stroke.Tool != "highlighter"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrawStroke(pixels, stroke, scaleX, scaleY);
        }

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, ImageWidth, ImageHeight, 144, 144, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        var encoded = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(encoded);
        return encoded;
    }

    private static void WritePdf(string path, IReadOnlyList<byte[]> images, CancellationToken cancellationToken)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var offsets = new long[3 + images.Count * 3];
        void Text(string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            output.Write(bytes, 0, bytes.Length);
        }
        void BeginObject(int id) { offsets[id] = output.Position; Text($"{id} 0 obj\n"); }
        void EndObject() => Text("endobj\n");

        Text("%PDF-1.4\n");
        output.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        BeginObject(1); Text("<< /Type /Catalog /Pages 2 0 R >>\n"); EndObject();
        BeginObject(2);
        var kids = string.Join(' ', Enumerable.Range(0, images.Count).Select(index => $"{3 + index * 3} 0 R"));
        Text($"<< /Type /Pages /Count {images.Count} /Kids [{kids}] >>\n"); EndObject();

        for (var index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageId = 3 + index * 3;
            var contentId = pageId + 1;
            var imageId = pageId + 2;
            BeginObject(pageId);
            Text(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PdfWidth} {PdfHeight}] /Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\n"));
            EndObject();
            var command = FormattableString.Invariant($"q {PdfWidth} 0 0 {PdfHeight} 0 0 cm /Im0 Do Q\n");
            BeginObject(contentId); Text($"<< /Length {Encoding.ASCII.GetByteCount(command)} >>\nstream\n{command}endstream\n"); EndObject();
            BeginObject(imageId);
            Text($"<< /Type /XObject /Subtype /Image /Width {ImageWidth} /Height {ImageHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {images[index].Length} >>\nstream\n");
            output.Write(images[index], 0, images[index].Length);
            Text("\nendstream\n"); EndObject();
        }

        var xref = output.Position;
        Text($"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
        for (var id = 1; id < offsets.Length; id++) Text($"{offsets[id]:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Flush(flushToDisk: true);
    }

    private static void ValidatePdf(string path, int expectedPages)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 128) throw new InvalidDataException("The generated PDF is incomplete.");

        var header = new byte[8];
        if (stream.Read(header) != header.Length || !Encoding.ASCII.GetString(header).StartsWith("%PDF-1.", StringComparison.Ordinal))
            throw new InvalidDataException("The generated file has an invalid PDF header.");

        var tailLength = (int)Math.Min(4096, stream.Length);
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        stream.ReadExactly(tail);
        var tailText = Encoding.ASCII.GetString(tail);
        var startXrefMarker = tailText.LastIndexOf("startxref\n", StringComparison.Ordinal);
        var eofMarker = tailText.LastIndexOf("%%EOF", StringComparison.Ordinal);
        if (startXrefMarker < 0 || eofMarker < startXrefMarker)
            throw new InvalidDataException("The generated PDF is missing its cross-reference trailer.");

        var offsetStart = startXrefMarker + "startxref\n".Length;
        var offsetEnd = tailText.IndexOf('\n', offsetStart);
        if (offsetEnd < 0 || !long.TryParse(tailText[offsetStart..offsetEnd], NumberStyles.None, CultureInfo.InvariantCulture, out var xrefOffset) ||
            xrefOffset <= 0 || xrefOffset >= stream.Length)
            throw new InvalidDataException("The generated PDF has an invalid cross-reference offset.");

        stream.Seek(xrefOffset, SeekOrigin.Begin);
        var xrefHeader = new byte[5];
        stream.ReadExactly(xrefHeader);
        if (!xrefHeader.SequenceEqual("xref\n"u8.ToArray()))
            throw new InvalidDataException("The generated PDF cross-reference table cannot be read.");

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = reader.ReadToEnd();
        var declaredPages = $"/Count {expectedPages}";
        if (!text.Contains(declaredPages, StringComparison.Ordinal))
            throw new InvalidDataException("The generated PDF page tree is incomplete.");
    }

    private static void DrawPaper(byte[] pixels, PaperStyle style, double scaleX, double scaleY)
    {
        if (style == PaperStyle.Blank) return;
        // Keep the pattern subtle, but strong enough to survive JPEG compression in the PDF.
        var (r, g, b) = ((byte)58, (byte)58, (byte)54);
        var lineWidth = Math.Max(1.5, (scaleX + scaleY) / 2);
        if (style == PaperStyle.Ruled || style == PaperStyle.Grid)
        {
            for (var y = 32d; y < InkPageControl.PaperHeight; y += 32)
                DrawLine(pixels, 0, y * scaleY, ImageWidth - 1, y * scaleY, r, g, b, 255, lineWidth);
        }
        if (style == PaperStyle.Grid)
        {
            for (var x = 32d; x < InkPageControl.PaperWidth; x += 32)
                DrawLine(pixels, x * scaleX, 0, x * scaleX, ImageHeight - 1, r, g, b, 255, lineWidth);
        }
        if (style == PaperStyle.Dotted)
            for (var y = 24d; y < InkPageControl.PaperHeight; y += 24)
                for (var x = 24d; x < InkPageControl.PaperWidth; x += 24)
                    DrawDisc(pixels, x * scaleX, y * scaleY, 1.6 * (scaleX + scaleY) / 4,
                        r, g, b, 255);
    }

    private static void DrawStroke(byte[] canvas, InkStrokeData stroke, double scaleX, double scaleY)
    {
        if (stroke.Points.Count == 0) return;
        var color = stroke.Tool == "highlighter" ? ParseColor(stroke.Color) : ((byte)239, (byte)239, (byte)234);
        var alpha = stroke.Tool == "highlighter" ? (byte)220 : (byte)255;
        var width = Math.Max(1.2, stroke.Width * (stroke.Tool == "highlighter" ? 1 : 1.15) * (scaleX + scaleY) / 2);
        for (var index = 1; index < stroke.Points.Count; index++)
        {
            var a = stroke.Points[index - 1];
            var b = stroke.Points[index];
            DrawLine(canvas, a.X * scaleX, a.Y * scaleY, b.X * scaleX, b.Y * scaleY,
                color.Item1, color.Item2, color.Item3, alpha, width);
        }
    }

    private static void DrawLine(byte[] canvas, double x0, double y0, double x1, double y1, byte r, byte g, byte b, byte alpha, double width)
    {
        var distance = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, width * .35)));
        for (var step = 0; step <= steps; step++)
        {
            var t = step / (double)steps;
            DrawDisc(canvas, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, Math.Max(.7, width / 2), r, g, b, alpha);
        }
    }

    private static async Task CompositePhotoAsync(byte[] canvas, PagePhotoData photo, double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        var path = PhotoFileService.PhotoPath(photo);
        if (!File.Exists(path)) return;
        try
        {
            await using var file = File.OpenRead(path);
            using var source = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var width = Math.Max(1u, (uint)Math.Round(photo.Width * scaleX));
            var height = Math.Max(1u, (uint)Math.Round(photo.Height * scaleY));
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            var sourcePixels = data.DetachPixelData();
            var centerX = (photo.X + photo.Width / 2) * scaleX;
            var centerY = (photo.Y + photo.Height / 2) * scaleY;
            var radians = photo.Rotation * Math.PI / 180d;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            for (var y = 0; y < (int)height; y++)
            for (var x = 0; x < (int)width; x++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localX = x - width / 2d;
                var localY = y - height / 2d;
                var destinationX = (int)Math.Round(centerX + localX * cos - localY * sin);
                var destinationY = (int)Math.Round(centerY + localX * sin + localY * cos);
                if ((uint)destinationX >= ImageWidth || (uint)destinationY >= ImageHeight) continue;
                var sourceIndex = (y * (int)width + x) * 4;
                Blend(canvas, (destinationY * ImageWidth + destinationX) * 4,
                    sourcePixels[sourceIndex + 2], sourcePixels[sourceIndex + 1], sourcePixels[sourceIndex], sourcePixels[sourceIndex + 3]);
            }
        }
        catch { }
    }

    private static void DrawDisc(byte[] canvas, double centerX, double centerY, double radius, byte r, byte g, byte b, byte alpha)
    {
        var minX = Math.Max(0, (int)Math.Floor(centerX - radius));
        var maxX = Math.Min(ImageWidth - 1, (int)Math.Ceiling(centerX + radius));
        var minY = Math.Max(0, (int)Math.Floor(centerY - radius));
        var maxY = Math.Min(ImageHeight - 1, (int)Math.Ceiling(centerY + radius));
        var radiusSquared = radius * radius;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var dx = x + .5 - centerX;
            var dy = y + .5 - centerY;
            if (dx * dx + dy * dy <= radiusSquared) Blend(canvas, (y * ImageWidth + x) * 4, r, g, b, alpha);
        }
    }

    private static void Blend(byte[] canvas, int index, byte r, byte g, byte b, byte alpha)
    {
        var amount = alpha / 255d;
        canvas[index] = (byte)Math.Round(b * amount + canvas[index] * (1 - amount));
        canvas[index + 1] = (byte)Math.Round(g * amount + canvas[index + 1] * (1 - amount));
        canvas[index + 2] = (byte)Math.Round(r * amount + canvas[index + 2] * (1 - amount));
        canvas[index + 3] = 255;
    }

    private static (byte, byte, byte) ParseColor(string value)
    {
        var hex = value.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        return hex.Length == 6
            ? (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16))
            : ((byte)255, (byte)122, (byte)24);
    }

    private static void Fill(byte[] pixels, byte r, byte g, byte b, byte a)
    {
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = b; pixels[index + 1] = g; pixels[index + 2] = r; pixels[index + 3] = a;
        }
    }
}
