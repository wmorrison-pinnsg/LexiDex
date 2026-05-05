using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LexiDex;

/// <summary>
/// Embedding generation using BGE-small-en-v1.5 via ONNX Runtime.
/// Runs entirely locally — no API keys or network calls needed.
/// </summary>
public sealed class BgeEmbeddingService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly Tokenizer _tokenizer;
    private const int MaxTokens = 512;
    private const int EmbeddingDim = 768;

    // BGE requires this prefix for retrieval queries
    private const string QueryPrefix = "Represent the sentence: ";

    public BgeEmbeddingService(string modelDir)
    {
        var modelPath = Path.Combine(modelDir, "model.onnx");
        var sessionOptions = new SessionOptions();
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = new Tokenizer(Path.Combine(modelDir, "tokenizer.json"));

        Console.WriteLine($"ONNX model loaded ({EmbeddingDim}d embeddings, max {MaxTokens} tokens)");
    }

    public async Task<IList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(
        IList<string> texts,
        CancellationToken cancellationToken = default)
    {
        var results = new ReadOnlyMemory<float>[texts.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, texts.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = cancellationToken
            },
            (i, ct) =>
            {
                results[i] = Embed(texts[i]);
                return ValueTask.CompletedTask;
            });

        return results;
    }

    private ReadOnlyMemory<float> Embed(string text)
    {
        var (inputIds, attentionMask) = _tokenizer.Encode(text, MaxTokens);

        // token_type_ids: all zeros for single sentence
        var tokenTypeIds = new long[inputIds.Length];

        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, inputIds.Length]);
        var attnMaskTensor = new DenseTensor<long>(attentionMask, [1, attentionMask.Length]);
        var typeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, tokenTypeIds.Length]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attnMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", typeIdsTensor),
        };

        using var results = _session.Run(inputs);

        // Output: last_hidden_state [1, seq_len, 384]
        var output = results.First().AsTensor<float>();

        // Mean pooling over non-padded tokens
        var embedding = new float[EmbeddingDim];
        var validTokenCount = (int)attentionMask.Count(m => m == 1);

        for (int t = 0; t < validTokenCount; t++)
        {
            for (int d = 0; d < EmbeddingDim; d++)
            {
                embedding[d] += output[0, t, d];
            }
        }

        for (int d = 0; d < EmbeddingDim; d++)
        {
            embedding[d] /= validTokenCount;
        }

        // L2 normalize (required for cosine similarity with BGE)
        var norm = MathF.Sqrt(embedding.Sum(x => x * x));
        if (norm > 1e-10f)
        {
            for (int d = 0; d < EmbeddingDim; d++)
            {
                embedding[d] /= norm;
            }
        }

        return new ReadOnlyMemory<float>(embedding);
    }

    public static string PrepareQueryText(string query) => QueryPrefix + query;

    public void Dispose()
    {
        _session.Dispose();
    }
}
