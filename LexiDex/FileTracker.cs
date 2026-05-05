using Microsoft.Data.Sqlite;

namespace SemanticSearch;

public enum FileStatus { New, Modified, Unchanged }

public class FileTracker
{
    private readonly SqliteConnection _connection;

    public FileTracker(SqliteConnection connection)
    {
        _connection = connection;
    }

    public async Task EnsureTableAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS file_metadata (
                file_path TEXT PRIMARY KEY,
                file_hash TEXT NOT NULL,
                last_modified INTEGER NOT NULL,
                chunk_count INTEGER NOT NULL,
                indexed_at INTEGER NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, FileStatus>> GetFileStatusesAsync(IEnumerable<string> filePaths)
    {
        var statuses = new Dictionary<string, FileStatus>(StringComparer.OrdinalIgnoreCase);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT file_path, file_hash, last_modified FROM file_metadata";
        var existing = new Dictionary<string, (string Hash, long Modified)>(StringComparer.OrdinalIgnoreCase);

        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                existing[reader.GetString(0)] = (reader.GetString(1), reader.GetInt64(2));
            }
        }

        foreach (var filePath in filePaths)
        {
            if (!existing.TryGetValue(filePath, out var meta))
            {
                statuses[filePath] = FileStatus.New;
                continue;
            }

            // Fast check: if timestamp unchanged, skip hash computation
            var currentModified = File.GetLastWriteTimeUtc(filePath).Ticks;
            if (currentModified == meta.Modified)
            {
                statuses[filePath] = FileStatus.Unchanged;
                continue;
            }

            // Timestamp changed — verify with hash
            var currentHash = ComputeHash(filePath);
            statuses[filePath] = currentHash == meta.Hash ? FileStatus.Unchanged : FileStatus.Modified;
        }

        return statuses;
    }

    public async Task UpdateFileRecordAsync(string filePath, string hash, long lastModified, int chunkCount)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO file_metadata (file_path, file_hash, last_modified, chunk_count, indexed_at)
            VALUES (@path, @hash, @modified, @count, @indexed)
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@modified", lastModified);
        cmd.Parameters.AddWithValue("@count", chunkCount);
        cmd.Parameters.AddWithValue("@indexed", DateTime.UtcNow.Ticks);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<string>> GetIndexedPathsAsync()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM file_metadata";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            paths.Add(reader.GetString(0));
        }
        return paths;
    }

    public async Task RemoveFileRecordsAsync(IEnumerable<string> filePaths)
    {
        foreach (var filePath in filePaths)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM file_metadata WHERE file_path = @path";
            cmd.Parameters.AddWithValue("@path", filePath);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
