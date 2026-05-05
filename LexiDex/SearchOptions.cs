namespace SemanticSearch;

public class SearchOptions
{
    public string Directory { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int TopK { get; set; } = 5;
}
