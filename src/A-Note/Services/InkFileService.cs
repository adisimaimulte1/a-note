using System.Text.Json;
using ANote.Models;
using ANote.Storage;

namespace ANote.Services;

public static class InkFileService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static async Task<List<InkStrokeData>> LoadAsync(string pageId)
    {
        var path = AppPaths.InkPath(pageId);
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<InkStrokeData>>(stream, Options) ?? [];
        }
        catch (JsonException)
        {
            var backup = path + ".bak";
            if (!File.Exists(backup)) return [];
            await using var stream = File.OpenRead(backup);
            return await JsonSerializer.DeserializeAsync<List<InkStrokeData>>(stream, Options) ?? [];
        }
    }

    public static async Task SaveAtomicAsync(string pageId, IReadOnlyList<InkStrokeData> strokes)
    {
        AppPaths.EnsureCreated();
        var target = AppPaths.InkPath(pageId);
        var temp = target + ".tmp";
        var backup = target + ".bak";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        {
            await JsonSerializer.SerializeAsync(stream, strokes, Options);
            await stream.FlushAsync();
        }
        if (File.Exists(target)) File.Replace(temp, target, backup, true);
        else File.Move(temp, target);
    }
}
