using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.Sqlite;

namespace SemanticSearch;

/// <summary>
/// Core semantic search engine — indexes documents into a SQLite vector store and queries them.
/// Supports incremental re-indexing by tracking file hashes.
/// </summary>
public class SearchEngine : IDisposable
{
    private readonly BgeEmbeddingService _embeddingService;
    private readonly string _modelDir;
    private string _dbPath = "";
    private SqliteVectorStore? _vectorStore;
    private IVectorStoreRecordCollection<string, DocumentChunk>? _collection;
    private SqliteConnection? _connection;
    private FileTracker? _fileTracker;

    public SearchEngine(string modelDir)
    {
        _modelDir = modelDir;
        _embeddingService = new BgeEmbeddingService(modelDir);
    }

    public string GetDbPath(string directory)
    {
        var hash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(directory))))
            .Replace('/', '_').Replace('+', '-').Replace("=", "")[..16];

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!File.Exists(Path.Combine(_modelDir, "model.onnx")))
            baseDir = ".";

        return Path.Combine(baseDir, $"semanticsearch_{hash}.db");
    }

    public bool IndexExists(string directory) => File.Exists(GetDbPath(directory));

    public async Task IndexAsync(string directory, IndexOptions? options = null, IProgress<IndexingProgress>? progress = null)
    {
        options ??= new IndexOptions { Directory = directory };
        _dbPath = GetDbPath(directory);

        var extensions = options.FileExtensions;
        var allFiles = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (allFiles.Count == 0)
        {
            Console.WriteLine("No files found to index.");
            return;
        }

        progress?.Report(new IndexingProgress(0, allFiles.Count, $"Found {allFiles.Count} files"));

        await InitVectorStoreAsync();

        List<string> filesToProcess;
        int unchangedCount = 0;

        if (options.Force)
        {
            filesToProcess = allFiles;
            Console.WriteLine("Force flag set — re-indexing all files.");
        }
        else
        {
            var statuses = await _fileTracker!.GetFileStatusesAsync(allFiles);
            filesToProcess = statuses
                .Where(kv => kv.Value != FileStatus.Unchanged)
                .Select(kv => kv.Key)
                .ToList();
            unchangedCount = statuses.Count(kv => kv.Value == FileStatus.Unchanged);
        }

        // Detect deleted files
        var indexedPaths = await _fileTracker!.GetIndexedPathsAsync();
        var deletedFiles = indexedPaths
            .Where(p => !allFiles.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (filesToProcess.Count == 0 && deletedFiles.Count == 0)
        {
            progress?.Report(new IndexingProgress(0, 0, $"All {unchangedCount} files unchanged"));
            Console.WriteLine($"All {unchangedCount} files unchanged. Nothing to do.");
            return;
        }

        // Remove chunks for deleted files
        foreach (var deletedFile in deletedFiles)
        {
            await DeleteFileChunksAsync(deletedFile);
            await _fileTracker.RemoveFileRecordsAsync([deletedFile]);
        }

        if (deletedFiles.Count > 0)
            Console.WriteLine($"Removed {deletedFiles.Count} deleted file(s).");

        // Remove chunks for modified files (will be re-indexed below)
        if (!options.Force)
        {
            var statuses = await _fileTracker.GetFileStatusesAsync(allFiles);
            var modifiedFiles = statuses.Where(kv => kv.Value == FileStatus.Modified).Select(kv => kv.Key);
            foreach (var modifiedFile in modifiedFiles)
            {
                await DeleteFileChunksAsync(modifiedFile);
            }
        }

        // Index new and modified files
        var chunks = new List<DocumentChunk>();
        foreach (var file in filesToProcess)
        {
            var fileChunks = FileIndexer.IndexFile(file, options);
            if (fileChunks.Count == 0) continue;

            var fileHash = FileTracker.ComputeHash(file);
            var lastModified = File.GetLastWriteTimeUtc(file).Ticks;
            foreach (var chunk in fileChunks)
            {
                chunk.FileHash = fileHash;
                chunk.LastModified = lastModified;
            }

            chunks.AddRange(fileChunks);
            progress?.Report(new IndexingProgress(0, filesToProcess.Count, Path.GetFileName(file)));

            await _fileTracker.UpdateFileRecordAsync(file, fileHash, lastModified, fileChunks.Count);
        }

        if (chunks.Count == 0)
        {
            Console.WriteLine("No new content to index.");
            return;
        }

        const int batchSize = 32;
        for (int i = 0; i < chunks.Count; i += batchSize)
        {
            var batch = chunks.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(c => c.Content).ToList();
            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(texts);

            for (int j = 0; j < batch.Count; j++)
            {
                batch[j].Embedding = embeddings[j];
            }

            progress?.Report(new IndexingProgress(Math.Min(i + batchSize, chunks.Count), chunks.Count, "Generating embeddings"));
        }

        foreach (var chunk in chunks)
        {
            await _collection!.UpsertAsync(chunk);
        }

        progress?.Report(new IndexingProgress(chunks.Count, chunks.Count, $"Done — {filesToProcess.Count} file(s) processed ({unchangedCount} unchanged, {deletedFiles.Count} deleted), {chunks.Count} chunks indexed"));
    }

    public async Task<List<SearchResult>> SearchAsync(string query, int topK = 5)
    {
        if (_collection is null)
        {
            Console.WriteLine("No index loaded. Run 'semanticsearch index <directory>' first.");
            return [];
        }

        var queryText = BgeEmbeddingService.PrepareQueryText(query);
        var queryEmbeddings = await _embeddingService.GenerateEmbeddingsAsync([queryText]);
        var queryEmbedding = queryEmbeddings[0];

        var results = _collection.SearchEmbeddingAsync(queryEmbedding, topK, new VectorSearchOptions<DocumentChunk>());

        var searchResults = new List<SearchResult>();
        await foreach (var result in results)
        {
            searchResults.Add(new SearchResult
            {
                SourceFile = result.Record.SourceFile,
                Content = result.Record.Content,
                ChunkIndex = result.Record.ChunkIndex,
                Score = result.Score ?? 0
            });
        }

        return searchResults.OrderBy(r => r.Score).ToList();
    }

    public async Task LoadExistingIndexAsync(string directory)
    {
        _dbPath = GetDbPath(directory);

        if (!File.Exists(_dbPath))
        {
            Console.WriteLine($"No existing index found at {_dbPath}");
            return;
        }

        await InitVectorStoreAsync();
        Console.WriteLine($"Loaded existing index from {_dbPath}");
    }

    private async Task InitVectorStoreAsync()
    {
        if (_vectorStore is not null) return;

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        await _connection.OpenAsync();

        var nativeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native");
        if (!Directory.Exists(nativeDir))
            nativeDir = Path.Combine("Native");

        var vecPath = Path.Combine(nativeDir, "vec0.dll");
        if (!File.Exists(vecPath))
            vecPath = Path.Combine(nativeDir, "vec0");

        _connection.LoadExtension(vecPath);

        _vectorStore = new SqliteVectorStore($"Data Source={_dbPath}");
        _collection = _vectorStore.GetCollection<string, DocumentChunk>("documents");
        await _collection.CreateCollectionIfNotExistsAsync();

        _fileTracker = new FileTracker(_connection);
        await _fileTracker.EnsureTableAsync();
    }

    private async Task DeleteFileChunksAsync(string filePath)
    {
        // Delete chunks by querying for matching source file
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "DELETE FROM documents WHERE SourceFile = @path";
        cmd.Parameters.AddWithValue("@path", filePath);
        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _embeddingService.Dispose();
    }
}

public class SearchResult
{
    public string SourceFile { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public double Score { get; set; }
}

public record IndexingProgress(int Current, int Total, string Message);
