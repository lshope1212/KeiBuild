using System.Xml;
using AngleSharp.Html.Parser;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;

namespace KeiBuild.Research.Collection;

public interface IResearchCollector
{
    Task<CollectedDocument> FetchAsync(DiscoveredResource resource, SourcePolicy policy, int maximumBytes, CancellationToken ct);
}

public sealed class ResearchHttpCollector(HttpDocumentCollector collector) : IResearchCollector
{
    public Task<CollectedDocument> FetchAsync(DiscoveredResource resource, SourcePolicy policy, int maximumBytes, CancellationToken ct)
    {
        var source = Source(resource, policy);
        return collector.CollectAsync(source, new(maximumBytes, policy.DelaySeconds, resource.Kind == ResourceKind.Sitemap, response =>
        {
            PublisherHeaders.CheckRestrictions(response);
            if (policy.Key.StartsWith("auto-header-", StringComparison.Ordinal) && PublisherHeaders.OpenFormat(response) != policy.Format)
                throw new IngestionException("The publisher's processing or reuse grant changed.") { RequiresSourceReview = true };
        }), ct);
    }

    internal Task<DocumentFormat?> InspectAsync(Uri uri, CancellationToken ct) =>
        collector.InspectHeadersAsync(uri, PublisherHeaders.OpenFormat, ct);

    public static SourceDefinition Source(DiscoveredResource resource, SourcePolicy policy)
    {
        var title = string.IsNullOrWhiteSpace(resource.Title) ? policy.Publisher : resource.Title;
        return new SourceDefinition(policy.Key, policy.Publisher, BraveSearchProvider.Clean(title, 150), resource.Url,
            policy.Kind, policy.Format, policy.AllowFetch, policy.PermissionNotes, policy.AllowSnapshot,
            policy.Language, resource.LastRetrievedAt);
    }
}

public static class LinkDiscovery
{
    public const int MaxLinks = 25;

    public static IReadOnlyList<SearchHit> FromHtml(CollectedDocument document, Uri url)
    {
        var parsed = new HtmlParser().ParseDocument(DocumentBytes.Decode(document.Bytes, document.Charset));
        if (parsed.QuerySelectorAll("meta[name]").Any(meta =>
            (string.Equals(meta.GetAttribute("name"), "robots", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(meta.GetAttribute("name"), RobotsRules.ProductToken, StringComparison.OrdinalIgnoreCase)) &&
            HasNofollow(meta.GetAttribute("content"), [',', ' '])))
            return [];
        return parsed.QuerySelectorAll("a[href]").Take(500)
            .Where(anchor => anchor.Closest("nav, footer, [hidden]") is null &&
                             !HasNofollow(anchor.GetAttribute("rel"), [' ', '\t', '\n', '\r']))
            .Select(anchor => (Url: UrlRules.Normalize(anchor.GetAttribute("href"), url), Title: BraveSearchProvider.Clean(anchor.TextContent.Trim(), 200)))
            .Where(link => link.Url is not null && link.Url.IdnHost == url.IdnHost && link.Url != url)
            .Select(link => new SearchHit(link.Url!.AbsoluteUri, link.Title, "Linked from an approved document."))
            .DistinctBy(hit => hit.Url).Take(MaxLinks).ToArray();
    }

    private static bool HasNofollow(string? value, char[] separators) =>
        value?.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => token.Equals("nofollow", StringComparison.OrdinalIgnoreCase) || token.Equals("none", StringComparison.OrdinalIgnoreCase)) == true;

    public static IReadOnlyList<SearchHit> FromSitemap(CollectedDocument document, Uri origin)
    {
        try
        {
            using var stream = new MemoryStream(document.Bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = DocumentBytes.MaxBytes
            });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "urlset")
                throw new IngestionException("Use a registered URL-set sitemap. Sitemap indexes are outside this workflow.");
            var results = new List<SearchHit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 2 || reader.LocalName != "loc")
                {
                    reader.Read();
                    continue;
                }
                var url = UrlRules.Normalize(reader.ReadElementContentAsString());
                if (url is not null && url.IdnHost == origin.IdnHost && seen.Add(url.AbsoluteUri))
                    results.Add(new(url.AbsoluteUri, "", "Discovered in a registered sitemap."));
                if (results.Count >= MaxLinks)
                    break;
            }
            return results;
        }
        catch (XmlException error)
        {
            throw new IngestionException("The registered sitemap is not a supported XML URL set.", error);
        }
    }
}
