namespace SemanticSearch;

public class IndexOptions
{
    public string Directory { get; set; } = string.Empty;
    public int ChunkSize { get; set; } = 500;
    public int ChunkOverlap { get; set; } = 50;
    public bool IncludeComments { get; set; }
    public bool Force { get; set; }

    public static readonly string[] DefaultExtensions =
    [
        ".txt", ".md", ".pdf",
        ".cs", ".py", ".ts", ".js", ".java", ".go", ".rs",
        ".cpp", ".c", ".h", ".rb", ".php", ".swift", ".kt",
        ".sql", ".sh", ".yaml", ".yml", ".json", ".xml", ".toml"
    ];

    public string[] FileExtensions { get; set; } = DefaultExtensions;
}
