namespace CapabilityResolver.Loading;

/// <summary>One non-blank source line with its comment removed and its indentation measured in columns.</summary>
public readonly record struct IndentedLine(int Number, int Indent, string Text);

/// <summary>Splits indentation-structured model files (UVL, Clafer, HAnS) into lines. A tab counts as <see cref="TabWidth"/> columns.</summary>
public static class IndentedText
{
    public const int TabWidth = 4;

    public static List<IndentedLine> Read(string text, params string[] lineComments)
    {
        var lines = new List<IndentedLine>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = StripComment(raw.TrimEnd('\r'), lineComments);

            var indent = 0;
            var start = 0;
            for (; start < line.Length && line[start] is ' ' or '\t'; start++)
            {
                indent += line[start] == '\t' ? TabWidth : 1;
            }

            var content = line[start..].TrimEnd();
            if (content.Length > 0) lines.Add(new IndentedLine(number, indent, content));
        }
        return lines;
    }

    private static string StripComment(string line, string[] prefixes)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (prefixes.Any(p => string.CompareOrdinal(line, i, p, 0, p.Length) == 0))
            {
                return line[..i];
            }
        }
        return line;
    }
}
