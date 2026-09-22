using ANote.Models;
using ANote.Storage;
using Microsoft.Data.Sqlite;

namespace ANote.Services;

public sealed class DatabaseService
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DatabaseService()
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS notebooks (
                    id TEXT PRIMARY KEY, title TEXT NOT NULL, subject TEXT, accent TEXT NOT NULL,
                    sort_order INTEGER NOT NULL, modified_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS pages (
                    id TEXT PRIMARY KEY, notebook_id TEXT NOT NULL, sort_order INTEGER NOT NULL,
                    paper_style INTEGER NOT NULL, modified_at TEXT NOT NULL,
                    FOREIGN KEY(notebook_id) REFERENCES notebooks(id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS ix_pages_notebook_order ON pages(notebook_id, sort_order);
                CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();

            var hasFavoriteColumn = false;
            using (var schema = db.CreateCommand())
            {
                schema.CommandText = "PRAGMA table_info(notebooks);";
                using var reader = await schema.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (string.Equals(reader.GetString(1), "is_favorite", StringComparison.OrdinalIgnoreCase))
                    {
                        hasFavoriteColumn = true;
                        break;
                    }
                }
            }
            if (!hasFavoriteColumn)
            {
                using var migrate = db.CreateCommand();
                migrate.CommandText = "ALTER TABLE notebooks ADD COLUMN is_favorite INTEGER NOT NULL DEFAULT 0;";
                await migrate.ExecuteNonQueryAsync();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<List<Notebook>> GetNotebooksAsync(string? search = null)
    {
        var result = new List<Notebook>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT n.id,n.title,n.subject,n.accent,n.sort_order,n.modified_at,n.is_favorite,COUNT(p.id)
            FROM notebooks n LEFT JOIN pages p ON p.notebook_id=n.id
            WHERE $search='' OR n.title LIKE $like OR COALESCE(n.subject,'') LIKE $like
            GROUP BY n.id ORDER BY n.sort_order,n.modified_at DESC;
            """;
        cmd.Parameters.AddWithValue("$search", search ?? "");
        cmd.Parameters.AddWithValue("$like", $"%{search}%");
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new Notebook
            {
                Id = reader.GetString(0), Title = reader.GetString(1),
                Subject = reader.IsDBNull(2) ? null : reader.GetString(2), Accent = reader.GetString(3),
                SortOrder = reader.GetInt32(4), ModifiedAt = DateTimeOffset.Parse(reader.GetString(5)),
                IsFavorite = reader.GetInt32(6) != 0, PageCount = reader.GetInt32(7)
            });
        return result;
    }

    public async Task SaveNotebookAsync(Notebook notebook)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO notebooks(id,title,subject,accent,sort_order,modified_at,is_favorite)
                VALUES($id,$title,$subject,$accent,$order,$modified,$favorite)
                ON CONFLICT(id) DO UPDATE SET title=$title,subject=$subject,accent=$accent,
                sort_order=$order,modified_at=$modified,is_favorite=$favorite;
                """;
            cmd.Parameters.AddWithValue("$id", notebook.Id);
            cmd.Parameters.AddWithValue("$title", notebook.Title);
            cmd.Parameters.AddWithValue("$subject", (object?)notebook.Subject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$accent", notebook.Accent);
            cmd.Parameters.AddWithValue("$order", notebook.SortOrder);
            cmd.Parameters.AddWithValue("$modified", notebook.ModifiedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$favorite", notebook.IsFavorite ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task SetNotebookFavoriteAsync(string notebookId, bool isFavorite)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE notebooks SET is_favorite=$favorite WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", notebookId);
            cmd.Parameters.AddWithValue("$favorite", isFavorite ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteNotebookAsync(string id)
    {
        var pages = await GetPagesAsync(id, false);
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM notebooks WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
        foreach (var page in pages)
        {
            var path = AppPaths.InkPath(page.Id);
            if (File.Exists(path)) File.Delete(path);
            await PhotoFileService.DeletePageAsync(page.Id);
        }
    }

    public async Task<List<NotePage>> GetPagesAsync(string notebookId, bool loadInk = true)
    {
        var result = new List<NotePage>();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,notebook_id,sort_order,paper_style,modified_at FROM pages WHERE notebook_id=$id ORDER BY sort_order";
        cmd.Parameters.AddWithValue("$id", notebookId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var page = new NotePage { Id = reader.GetString(0), NotebookId = reader.GetString(1), SortOrder = reader.GetInt32(2), PaperStyle = (PaperStyle)reader.GetInt32(3), ModifiedAt = DateTimeOffset.Parse(reader.GetString(4)) };
            if (loadInk)
            {
                page.Strokes = await InkFileService.LoadAsync(page.Id);
                page.Photos = await PhotoFileService.LoadAsync(page.Id);
            }
            result.Add(page);
        }
        return result;
    }

    public async Task SavePageAsync(NotePage page, bool saveInk = true)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pages(id,notebook_id,sort_order,paper_style,modified_at)
                VALUES($id,$notebook,$order,$style,$modified)
                ON CONFLICT(id) DO UPDATE SET sort_order=$order,paper_style=$style,modified_at=$modified;
                UPDATE notebooks SET modified_at=$modified WHERE id=$notebook;
                """;
            cmd.Parameters.AddWithValue("$id", page.Id);
            cmd.Parameters.AddWithValue("$notebook", page.NotebookId);
            cmd.Parameters.AddWithValue("$order", page.SortOrder);
            cmd.Parameters.AddWithValue("$style", (int)page.PaperStyle);
            cmd.Parameters.AddWithValue("$modified", page.ModifiedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
        if (saveInk)
        {
            await InkFileService.SaveAtomicAsync(page.Id, page.Strokes);
            await PhotoFileService.SaveAtomicAsync(page.Id, page.Photos);
        }
    }

    public async Task DeletePageAsync(NotePage page)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM pages WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", page.Id);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
        var path = AppPaths.InkPath(page.Id);
        if (File.Exists(path)) File.Delete(path);
        await PhotoFileService.DeletePageAsync(page.Id);
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await _gate.WaitAsync();
        try
        {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=$value";
            cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }
}
