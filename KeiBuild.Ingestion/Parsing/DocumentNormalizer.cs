using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Parsing;

public sealed record NormalizedDocument(IReadOnlyList<string> Lines, string? Language = null);

public static class DocumentNormalizer
{
    public const int MaxCharacters = 40_000;

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "br", "caption", "dd", "div", "dl",
        "dt", "figcaption", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
        "li", "main", "ol", "p", "pre", "section", "table", "ul"
    };

    public static NormalizedDocument Normalize(CollectedDocument document, DocumentFormat format)
    {
        var text = DocumentBytes.Decode(document.Bytes, document.Charset);
        var output = new LineBuffer();
        string? language = null;
        if (format == DocumentFormat.Html)
            language = HtmlLines(text, output);
        else
            TextLines(text, format, output);

        if (output.Lines.Count == 0)
            throw new IngestionException("The document has no extractable text.");
        return new NormalizedDocument(output.Lines, language);
    }

    private static void TextLines(string text, DocumentFormat format, LineBuffer output)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(Collapse).ToArray();
        string[]? headings = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (format == DocumentFormat.Markdown)
            {
                var cells = MarkdownCells(line);
                if (i > 0 && cells.Length > 0 && cells.All(IsColumnSeparator))
                {
                    var previousCells = MarkdownCells(lines[i - 1]);
                    headings = previousCells.Length == cells.Length ? previousCells : null;
                }
                else if (headings is not null && cells.Length == headings.Length)
                    line = FormatCells(headings, cells);
                else
                    headings = null;
            }
            output.Add(line);
        }
    }

    private static bool IsColumnSeparator(string cell)
    {
        var dashes = cell.Trim(':');
        return dashes.Length >= 3 && dashes.All(character => character == '-');
    }

    private static string[] MarkdownCells(string line) =>
        line.StartsWith('|') && line.EndsWith('|') && !line.Contains("\\|", StringComparison.Ordinal)
            ? line[1..^1].Split('|', StringSplitOptions.TrimEntries)
            : [];

    private static string? HtmlLines(string text, LineBuffer output)
    {
        // The default parser has no browsing context or resource loader.
        var document = new HtmlParser().ParseDocument(text);
        foreach (var element in document.QuerySelectorAll("script, style, nav, footer, template, noscript, form, iframe, svg, [hidden], [aria-hidden='true'], [role='navigation'], [role='contentinfo']"))
            element.Remove();

        if (document.Body is null)
            return null;

        var pending = new StringBuilder();
        var nodes = new Stack<(INode Node, bool End)>();
        var tableHeaders = new Dictionary<IElement, string[]>();
        nodes.Push((document.Body, false));
        while (nodes.TryPop(out var step))
        {
            if (step.Node is IText textNode)
            {
                pending.Append(textNode.Data);
                continue;
            }

            var element = step.Node as IElement;
            if (element is null)
                continue;
            if (element.LocalName == "tr")
            {
                Flush(pending, output);
                output.Add(TableRow(element, tableHeaders));
                continue;
            }

            if (BlockElements.Contains(element.LocalName))
            {
                Flush(pending, output);
                if (!step.End)
                    nodes.Push((element, true));
            }
            if (!step.End)
            {
                for (var i = element.ChildNodes.Length - 1; i >= 0; i--)
                    nodes.Push((element.ChildNodes[i], false));
            }
        }
        Flush(pending, output);
        var language = document.DocumentElement?.GetAttribute("lang")?.Trim();
        return language is { Length: > 0 and <= 40 } && language.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            ? language : null;
    }

    private static string TableRow(IElement row, Dictionary<IElement, string[]> tableHeaders)
    {
        var cells = row.Children.Where(cell => cell.LocalName is "td" or "th").ToArray();
        var values = cells.Select(cell => Collapse(cell.TextContent)).ToArray();
        var table = row.Closest("table");
        // Simple, aligned tables get column labels. Preserve other rows in their original order.
        if (values.Length == 0 || table is null || cells.Any(cell => cell.HasAttribute("colspan") || cell.HasAttribute("rowspan")))
            return string.Join(" | ", values);

        if (cells.All(cell => cell.LocalName == "th"))
        {
            tableHeaders[table] = values;
            return string.Join(" | ", values);
        }

        if (tableHeaders.TryGetValue(table, out var headers) && headers.Length == values.Length)
            return FormatCells(headers, values);

        return string.Join(" | ", values);
    }

    private static string FormatCells(string[] headers, string[] values) =>
        string.Join(" | ", values.Select((value, i) => headers[i].Length > 0 ? $"{headers[i]}: {value}" : value));

    private static void Flush(StringBuilder pending, LineBuffer output)
    {
        var line = Collapse(pending.ToString());
        pending.Clear();
        output.Add(line);
    }

    private static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class LineBuffer
    {
        private int characters;
        public List<string> Lines { get; } = [];

        public void Add(string line)
        {
            if (line.Length == 0)
                return;
            characters += line.Length + 1;
            if (characters > MaxCharacters)
                throw new IngestionException("The normalized document exceeds 40,000 characters. Supply a permitted excerpt with its heading and application table.");
            Lines.Add(line);
        }
    }
}
