using System.Text.Json;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Research.Data;

namespace KeiBuild.Research.Discovery;

// Search registered publisher metadata. Abstracts and full text are outside the metadata request.
internal sealed class CrossrefSearch(HttpDocumentCollector collector, string vehicleName, string engine) : ISearchProvider
{
    internal const string Provider = "crossref";

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        var terms = $"{vehicleName} {engine} {query.Text}";
        var uri = new Uri("https://api.crossref.org/works?rows=5&select=DOI,title,publisher,resource,URL&query.bibliographic=" +
            Uri.EscapeDataString(terms));
        var json = await collector.ReadPublisherJsonAsync(uri, ct);
        try
        {
            using var data = JsonDocument.Parse(json);
            var hits = new List<SearchHit>();
            foreach (var work in data.RootElement.GetProperty("message").GetProperty("items").EnumerateArray().Take(5))
            {
                var title = work.TryGetProperty("title", out var titles) && titles.GetArrayLength() > 0 ? titles[0].GetString() : null;
                var publisher = work.TryGetProperty("publisher", out var pub) ? pub.GetString() : null;
                var url = work.TryGetProperty("resource", out var resource) && resource.TryGetProperty("primary", out var primary) &&
                    primary.TryGetProperty("URL", out var landing) ? landing.GetString() : work.GetProperty("URL").GetString();
                if (UrlRules.Normalize(url) is {} target && !string.IsNullOrWhiteSpace(title))
                    hits.Add(new(target.AbsoluteUri, title, $"DOI {work.GetProperty("DOI").GetString()}. Publisher: {publisher}", publisher));
            }
            return hits;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new IngestionException("Crossref returned unexpected metadata.", ex) { IsTransient = true };
        }
    }
}

