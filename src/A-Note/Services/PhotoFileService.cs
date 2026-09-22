using System.Text.Json;
using ANote.Models;
using ANote.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ANote.Services;

public static class PhotoFileService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static string MetadataPath(string pageId) => Path.Combine(AppPaths.AttachmentDirectory, $"{pageId}.photos.json");
    public static string PhotoPath(PagePhotoData photo) => Path.Combine(AppPaths.AttachmentDirectory, photo.FileName);

    public static async Task<List<PagePhotoData>> LoadAsync(string pageId)
    {
        var path = MetadataPath(pageId);
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<PagePhotoData>>(stream, Options) ?? [];
        }
        catch (JsonException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) return [];
            await using var stream = File.OpenRead(backup);
            return await JsonSerializer.DeserializeAsync<List<PagePhotoData>>(stream, Options) ?? [];
        }
    }

    public static async Task SaveAtomicAsync(string pageId, IReadOnlyList<PagePhotoData> photos)
    {
        AppPaths.EnsureCreated();
        var target = MetadataPath(pageId);
        var temp = target + ".tmp";
        var backup = target + ".bak";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 32768, true))
        {
            await JsonSerializer.SerializeAsync(stream, photos, Options);
            await stream.FlushAsync();
        }
        if (File.Exists(target)) File.Replace(temp, target, backup, true);
        else File.Move(temp, target);
    }

    public static async Task<PagePhotoData> ImportAsync(string pageId, StorageFile source)
    {
        AppPaths.EnsureCreated();
        var extension = Path.GetExtension(source.Name);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var photo = new PagePhotoData { FileName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}" };
        var destination = Path.Combine(AppPaths.AttachmentDirectory, photo.FileName);
        // Vector formats have no mandatory bitmap pixel size. Give them a useful default;
        // raster formats below replace this using their decoded dimensions.
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            photo.Width = 420;
            photo.Height = 280;
            photo.X = (ANote.Ink.InkPageControl.PaperWidth - photo.Width) / 2d;
            photo.Y = (ANote.Ink.InkPageControl.PaperHeight - photo.Height) / 2d;
        }
        using (var input = await source.OpenStreamForReadAsync())
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            await input.CopyToAsync(output);
            await output.FlushAsync();
        }

        try
        {
            var copied = await StorageFile.GetFileFromPathAsync(destination);
            using var stream = await copied.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth > 0 && decoder.PixelHeight > 0)
            {
                const double maxWidth = 460;
                const double maxHeight = 360;
                var scale = Math.Min(1d, Math.Min(maxWidth / decoder.PixelWidth, maxHeight / decoder.PixelHeight));
                photo.Width = Math.Max(100, decoder.PixelWidth * scale);
                photo.Height = Math.Max(80, decoder.PixelHeight * scale);
                photo.X = Math.Max(20, (ANote.Ink.InkPageControl.PaperWidth - photo.Width) / 2d);
                photo.Y = Math.Max(20, (ANote.Ink.InkPageControl.PaperHeight - photo.Height) / 2d);
            }
        }
        catch { }

        return photo;
    }

    public static async Task<List<PagePhotoData>> CloneForPageAsync(string sourcePageId, string targetPageId, IReadOnlyList<PagePhotoData> sourcePhotos)
    {
        AppPaths.EnsureCreated();
        var result = new List<PagePhotoData>();
        foreach (var source in sourcePhotos)
        {
            var originalPath = PhotoPath(source);
            if (!File.Exists(originalPath)) continue;
            var extension = Path.GetExtension(source.FileName);
            var clone = new PagePhotoData
            {
                FileName = $"{Guid.NewGuid():N}{extension}",
                X = source.X,
                Y = source.Y,
                Width = source.Width,
                Height = source.Height,
                Rotation = source.Rotation
            };
            File.Copy(originalPath, PhotoPath(clone), overwrite: false);
            result.Add(clone);
        }
        await SaveAtomicAsync(targetPageId, result);
        return result;
    }

    public static async Task DeletePhotoAsync(PagePhotoData photo)
    {
        await Task.Yield();
        var path = PhotoPath(photo);
        if (File.Exists(path)) File.Delete(path);
    }

    public static async Task DeletePageAsync(string pageId)
    {
        var photos = await LoadAsync(pageId);
        foreach (var photo in photos)
        {
            var path = PhotoPath(photo);
            if (File.Exists(path)) File.Delete(path);
        }
        var metadata = MetadataPath(pageId);
        if (File.Exists(metadata)) File.Delete(metadata);
        var backup = metadata + ".bak";
        if (File.Exists(backup)) File.Delete(backup);
    }
}
