using System.Text.RegularExpressions;

namespace LexiDex;

public enum LanguageFamily
{
    CStyle,
    HashComment,
    XmlStyle,
    None
}

public static class CodeExtractor
{
    public static LanguageFamily GetLanguageFamily(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".java" or ".kt" or ".scala" or ".go" or ".rs" or ".cpp" or ".c" or ".h"
            => LanguageFamily.CStyle,
        ".js" or ".ts" or ".swift" or ".php"
            => LanguageFamily.CStyle,
        ".py" or ".rb" or ".sh" or ".yaml" or ".yml" or ".toml"
            => LanguageFamily.HashComment,
        ".xml" or ".html" or ".svg"
            => LanguageFamily.XmlStyle,
        ".sql" or ".json"
            => LanguageFamily.None,
        _ => LanguageFamily.None
    };

    public static string Extract(string filePath, LanguageFamily language, bool includeComments)
    {
        var text = File.ReadAllText(filePath);
        if (includeComments) return text;
        return language switch
        {
            LanguageFamily.CStyle => StripCStyleComments(text),
            LanguageFamily.HashComment => StripHashComments(text),
            LanguageFamily.XmlStyle => StripXmlComments(text),
            _ => text
        };
    }

    public static List<TextSegment> DetectStructure(string text, LanguageFamily language)
    {
        return language switch
        {
            LanguageFamily.CStyle => DetectCStyleStructure(text),
            LanguageFamily.HashComment => DetectHashStructure(text),
            _ => DetectByBlankLines(text)
        };
    }

    private static string StripCStyleComments(string text)
    {
        // Strip /* ... */ block comments
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        // Strip // line comments (only at start of trimmed line to avoid stripping URLs)
        text = Regex.Replace(text, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    private static string StripHashComments(string text)
    {
        text = Regex.Replace(text, @"^[ \t]*#.*$", "", RegexOptions.Multiline);
        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    private static string StripXmlComments(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);
        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }

    private static List<TextSegment> DetectCStyleStructure(string text)
    {
        var segments = new List<TextSegment>();
        // Match class/struct/interface/enum/function/method declarations
        var pattern = @"(?m)^\s*(?:\[.*?\]\s*)*(?:(?:public|private|protected|internal|static|async|virtual|override|abstract|sealed|partial|readonly|new)\s+)*(?:class|struct|interface|enum|void|int|long|float|double|bool|string|var|Task|async)\s+[\w<>\[\],\s]+\s*(?:\{|where\s)";
        var matches = Regex.Matches(text, pattern);

        if (matches.Count == 0)
            return DetectByBlankLines(text);

        var prevEnd = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.Index > prevEnd)
            {
                var between = text[prevEnd..match.Index].Trim();
                if (!string.IsNullOrWhiteSpace(between))
                    segments.Add(new TextSegment("imports/globals", between));
            }
            var label = match.Value.Trim();
            if (label.Length > 80) label = label[..80] + "...";
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            segments.Add(new TextSegment(label, text[match.Index..end].Trim()));
            prevEnd = end;
        }

        return segments.Count > 0 ? segments : DetectByBlankLines(text);
    }

    private static List<TextSegment> DetectHashStructure(string text)
    {
        var segments = new List<TextSegment>();
        // Python: detect class and def at start of line
        var pattern = @"(?m)^(class |def |async def )\w+";
        var matches = Regex.Matches(text, pattern);

        if (matches.Count == 0)
            return DetectByBlankLines(text);

        var prevEnd = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (match.Index > prevEnd)
            {
                var between = text[prevEnd..match.Index].Trim();
                if (!string.IsNullOrWhiteSpace(between))
                    segments.Add(new TextSegment("module-level", between));
            }
            var label = match.Value.Trim();
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            segments.Add(new TextSegment(label, text[match.Index..end].Trim()));
            prevEnd = end;
        }

        return segments.Count > 0 ? segments : DetectByBlankLines(text);
    }

    private static List<TextSegment> DetectByBlankLines(string text)
    {
        var segments = new List<TextSegment>();
        var parts = Regex.Split(text, @"\n\s*\n");

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                segments.Add(new TextSegment("block", trimmed));
        }

        return segments;
    }
}

public class TextSegment
{
    public string Label { get; }
    public string Content { get; }

    public TextSegment(string label, string content)
    {
        Label = label;
        Content = content;
    }
}
