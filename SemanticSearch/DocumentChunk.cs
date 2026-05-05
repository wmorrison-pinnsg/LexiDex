using Microsoft.Extensions.VectorData;

namespace SemanticSearch;

/// <summary>
/// Represents a chunk of text stored in the SQLite vector store.
/// </summary>
public class DocumentChunk
{
    [VectorStoreRecordKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreRecordData]
    public string SourceFile { get; set; } = string.Empty;

    [VectorStoreRecordData]
    public string Content { get; set; } = string.Empty;

    [VectorStoreRecordData]
    public int ChunkIndex { get; set; }

    [VectorStoreRecordData]
    public string FileHash { get; set; } = string.Empty;

    [VectorStoreRecordData]
    public long LastModified { get; set; }

    [VectorStoreRecordVector(Dimensions: 768, DistanceFunction = DistanceFunction.EuclideanDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
