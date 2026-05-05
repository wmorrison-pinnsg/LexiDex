using System.Text;
using Markdig;
using UglyToad.PdfPig;

namespace LexiDex;

/// <summary>
/// Reads local files (.txt, .md, .pdf), splits them into chunks, and returns them for embedding.
/// </summary>
public static class FileIndexer
{

    public static List<DocumentChunk> IndexDirectory(string directory, IndexOptions? options = null)
    {
        options ??= new IndexOptions();
        var chunks = new List<DocumentChunk>();

        var extensions = options.FileExtensions;
        var files = Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"Found {files.Count} files to index in {directory}");

        foreach (var file in files)
        {
            var fileChunks = IndexFile(file, options);
            chunks.AddRange(fileChunks);
            if (fileChunks.Count > 0)
                Console.WriteLine($"  Indexed: {Path.GetFileName(file)} ({fileChunks.Count} chunks)");
        }

        Console.WriteLine($"Total: {chunks.Count} chunks indexed");
        return chunks;
    }

    public static List<DocumentChunk> IndexFile(string filePath, IndexOptions options)
    {
        var text = ExtractText(filePath, options.IncludeComments);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var language = CodeExtractor.GetLanguageFamily(ext);

        if (language != LanguageFamily.None && ext is not (".json" or ".sql"))
        {
            return ChunkCode(text, filePath, options.ChunkSize, options.ChunkOverlap, language);
        }

        return ChunkText(text, filePath, options.ChunkSize, options.ChunkOverlap);
    }

    private static string ExtractText(string filePath, bool includeComments)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var language = CodeExtractor.GetLanguageFamily(ext);

        return ext switch
        {
            ".txt" => File.ReadAllText(filePath),
            ".md" => ExtractFromMarkdown(filePath),
            ".pdf" => ExtractFromPdf(filePath),
            _ when IsCodeFile(ext) => CodeExtractor.Extract(filePath, language, includeComments),
            _ => string.Empty
        };
    }

    private static bool IsCodeFile(string ext) => ext is
        ".cs" or ".java" or ".kt" or ".scala" or ".go" or ".rs" or
        ".cpp" or ".c" or ".h" or ".js" or ".ts" or ".swift" or ".php" or
        ".py" or ".rb" or ".sh" or ".yaml" or ".yml" or ".toml" or
        ".xml" or ".sql" or ".json";

    private static string ExtractFromMarkdown(string filePath)
    {
        var markdown = File.ReadAllText(filePath);
        // Convert to plain text — strip all MD formatting
        var pipeline = new MarkdownPipelineBuilder().Build();
        var html = Markdown.ToHtml(markdown, pipeline);
        // Strip HTML tags and decode entities
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "");
        return text.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                   .Replace("&quot;", "\"").Replace("&#39;", "'");
    }

    private static string ExtractFromPdf(string filePath)
    {
        var sb = new StringBuilder();
        using var document = PdfDocument.Open(filePath);
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private static List<DocumentChunk> ChunkCode(string text, string sourceFile, int chunkSize, int chunkOverlap, LanguageFamily language)
    {
        var structuralSegments = CodeExtractor.DetectStructure(text, language);
        var segments = new List<string>();

        foreach (var segment in structuralSegments)
        {
            var normalized = System.Text.RegularExpressions.Regex.Replace(segment.Content, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) continue;

            if (normalized.Length <= chunkSize)
            {
                segments.Add(normalized);
            }
            else
            {
                // Structural segment too large — hard split at word boundaries
                foreach (var piece in HardSplit(normalized, chunkSize))
                    segments.Add(piece);
            }
        }

        return AssembleChunks(segments, sourceFile, chunkSize, chunkOverlap);
    }

    private static List<DocumentChunk> ChunkText(string text, string sourceFile, int chunkSize, int chunkOverlap)
    {
        // Normalize whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        var segments = SplitIntoSegments(text, chunkSize);
        var chunks = AssembleChunks(segments, sourceFile, chunkSize, chunkOverlap);
        return chunks;
    }

    private static List<string> SplitIntoSegments(string text, int chunkSize)
    {
        var segments = new List<string>();

        // Split on paragraph boundaries (double newlines normalized to single space gaps)
        var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var paragraph in paragraphs)
        {
            var normalized = System.Text.RegularExpressions.Regex.Replace(paragraph, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(normalized)) continue;

            if (normalized.Length <= chunkSize)
            {
                segments.Add(normalized);
                continue;
            }

            // Paragraph too long — split at sentence boundaries
            var sentences = SplitAtSentences(normalized);
            foreach (var sentence in sentences)
            {
                if (sentence.Length <= chunkSize)
                {
                    segments.Add(sentence);
                    continue;
                }

                // Sentence too long — hard split at chunkSize at word boundaries
                foreach (var piece in HardSplit(sentence, chunkSize))
                {
                    segments.Add(piece);
                }
            }
        }

        return segments;
    }

    private static List<string> SplitAtSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;

        for (int i = 0; i < text.Length - 1; i++)
        {
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && text[i + 1] == ' ')
            {
                var end = i + 1;
                // Include trailing spaces
                while (end < text.Length && text[end] == ' ') end++;

                var sentence = text[start..end].Trim();
                if (!string.IsNullOrWhiteSpace(sentence))
                    sentences.Add(sentence);
                start = end;
            }
        }

        // Remaining text
        if (start < text.Length)
        {
            var remaining = text[start..].Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
                sentences.Add(remaining);
        }

        return sentences;
    }

    private static List<string> HardSplit(string text, int chunkSize)
    {
        var pieces = new List<string>();
        var position = 0;

        while (position < text.Length)
        {
            var length = Math.Min(chunkSize, text.Length - position);

            if (position + length < text.Length)
            {
                // Look for word boundary in last 20% of chunk
                var searchStart = position + (int)(length * 0.8);
                for (int i = position + length; i >= searchStart; i--)
                {
                    if (text[i] == ' ')
                    {
                        length = i - position;
                        break;
                    }
                }
            }

            var piece = text.Substring(position, length).Trim();
            if (!string.IsNullOrWhiteSpace(piece))
                pieces.Add(piece);

            position += length;
        }

        return pieces;
    }

    private static List<DocumentChunk> AssembleChunks(List<string> segments, string sourceFile, int chunkSize, int chunkOverlap)
    {
        var chunks = new List<DocumentChunk>();
        if (segments.Count == 0) return chunks;

        var currentParts = new List<string>();
        var currentLength = 0;
        var overlapPrefix = "";

        void Flush(int index)
        {
            var content = overlapPrefix + string.Join(" ", currentParts);
            content = content.Trim();
            if (string.IsNullOrWhiteSpace(content)) return;

            chunks.Add(new DocumentChunk
            {
                Id = $"{Path.GetFileName(sourceFile)}_{index}",
                SourceFile = sourceFile,
                Content = content,
                ChunkIndex = index
            });

            // Carry forward overlap from the end of this chunk
            if (chunkOverlap > 0 && content.Length > chunkOverlap)
            {
                overlapPrefix = content[^chunkOverlap..] + " ";
            }
            else
            {
                overlapPrefix = "";
            }
        }

        var chunkIndex = 0;
        foreach (var segment in segments)
        {
            if (currentLength + segment.Length + 1 > chunkSize && currentParts.Count > 0)
            {
                Flush(chunkIndex++);
                currentParts.Clear();
                currentLength = overlapPrefix.Length;
            }

            currentParts.Add(segment);
            currentLength += segment.Length + 1;
        }

        // Flush remaining
        if (currentParts.Count > 0)
        {
            Flush(chunkIndex);
        }

        return chunks;
    }
}
