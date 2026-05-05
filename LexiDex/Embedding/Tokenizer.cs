using System.Text.Json;

namespace LexiDex;

/// <summary>
/// Minimal BERT WordPiece tokenizer that reads HuggingFace tokenizer.json format.
/// Handles tokenization for BGE/BERT ONNX models without external tokenizer dependencies.
/// </summary>
public sealed class Tokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkTokenId;
    private readonly int _clsTokenId;
    private readonly int _sepTokenId;

    public Tokenizer(string tokenizerJsonPath)
    {
        var json = File.ReadAllText(tokenizerJsonPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Load vocabulary from model_vocab or vocab
        var vocabProp = root.TryGetProperty("model", out var model) && model.TryGetProperty("vocab", out var v)
            ? v
            : root.GetProperty("vocab");

        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in vocabProp.EnumerateObject())
        {
            _vocab[entry.Name] = entry.Value.GetInt32();
        }

        _unkTokenId = _vocab.TryGetValue("[UNK]", out var unk) ? unk : 100;
        _clsTokenId = _vocab.TryGetValue("[CLS]", out var cls) ? cls : 101;
        _sepTokenId = _vocab.TryGetValue("[SEP]", out var sep) ? sep : 102;
    }

    public (long[] InputIds, long[] AttentionMask) Encode(string text, int maxLength)
    {
        // Basic pre-tokenization: split on whitespace and punctuation
        var tokens = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // Try the whole word first
            if (_vocab.ContainsKey($"##{word}") || _vocab.ContainsKey(word.ToLowerInvariant()))
            {
                tokens.Add(word.ToLowerInvariant());
                continue;
            }

            // WordPiece: try progressively longer first pieces
            var remaining = word.ToLowerInvariant();
            var isFirst = true;

            while (remaining.Length > 0)
            {
                var found = false;
                for (int len = Math.Min(remaining.Length, remaining.Length); len >= 1; len--)
                {
                    var piece = isFirst ? remaining[..len] : $"##{remaining[..len]}";
                    if (_vocab.ContainsKey(piece))
                    {
                        tokens.Add(piece);
                        remaining = remaining[len..];
                        isFirst = false;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    tokens.Add("[UNK]");
                    remaining = remaining[1..];
                    isFirst = false;
                }
            }
        }

        // Convert to IDs, add [CLS] and [SEP]
        var inputIds = new List<long> { _clsTokenId };
        foreach (var token in tokens)
        {
            inputIds.Add(_vocab.TryGetValue(token, out var id) ? id : _unkTokenId);
        }
        inputIds.Add(_sepTokenId);

        // Truncate to maxLength
        if (inputIds.Count > maxLength)
        {
            inputIds = inputIds.Take(maxLength).ToList();
        }

        var attentionMask = inputIds.Select(_ => 1L).ToArray();
        return (inputIds.ToArray(), attentionMask);
    }
}
