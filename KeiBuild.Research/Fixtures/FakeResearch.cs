using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Collection;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;

namespace KeiBuild.Research.Fixtures;

public sealed class FakeResearch : ISearchProvider, IResearchCollector, IKnowledgeExtractor
{
    public const string Scope = "https://keibuild-research.example/ha4/";
    public string Name => "Offline synthetic fixture";

    public static PolicyInput Policy { get; } = new("synthetic-publisher", Scope, true,
        "SYNTHETIC — KeiBuild fixture publisher", SourceKind.Manufacturer, DocumentFormat.Html,
        "Fabricated local files for an offline pipeline exercise. No websites are contacted.",
        true, true, true, false, true, SitemapUrl: Scope + "sitemap.xml");

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<SearchHit> hits =
        [
            new(Scope + "manual", "SYNTHETIC HA4 E07A technical manual", "Vehicle specification and maintenance."),
            new(Scope + "mirror#specifications", "SYNTHETIC HA4 manual mirror", "Identical example content."),
            new(Scope + "parts", "SYNTHETIC HA4 parts catalog", "Part numbers and fitment."),
            new(Scope + "japanese", "SYNTHETIC アクティ HA4 整備資料", "Japanese fixture."),
            new("https://unapproved.example/ha4/manual", "HA4 E07A service manual", "No collection policy."),
            new("https://unapproved.example/unrelated", "Unrelated pickup", "Unrelated topic.")
        ];
        return Task.FromResult(hits);
    }

    public async Task<CollectedDocument> FetchAsync(DiscoveredResource resource, SourcePolicy policy, int maximumBytes, CancellationToken ct)
    {
        var filename = resource.Url switch
        {
            Scope + "manual" or Scope + "mirror" => "manual.html",
            Scope + "parts" => "parts.html",
            Scope + "japanese" => "japanese.html",
            Scope + "conflict" => "conflict.html",
            Scope + "sitemap.xml" => "sitemap.xml",
            _ => throw new IngestionException("No offline fixture matches this URL.")
        };
        var bytes = await DocumentBytes.ReadFileAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename), ct);
        if (bytes.Length > maximumBytes)
            throw DocumentBytes.SizeError(maximumBytes);
        return new CollectedDocument(bytes, DateTimeOffset.UtcNow, "utf-8");
    }

    public Task<KnowledgeResponse> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var facts = new List<FactDraft>();
        var fitments = new List<object>();
        var lines = input.Document.Lines;
        const string subject = "SYNTHETIC HA4 reference";
        const string scope = "HA4 E07A fictional 1998 manual reference";
        const string manufacturer = "Fixture Parts Co.";
        const string part = "00-SYNTH-HA4";
        const string partName = "SYNTHETIC HA4 filter";

        var clearance = Find("Demo clearance:");
        if (clearance > 0)
            facts.Add(new(FactKind.MechanicalSpecification, subject, "Demo clearance",
                lines[clearance - 1].Contains("21 mm") ? "21" : "22", "mm", null, null, null, scope, null, 1, clearance, clearance));
        var interval = Find("Demo interval:");
        if (interval > 0)
        {
            facts.Add(new(FactKind.MaintenanceInterval, subject, "Demo interval", "12000", "km",
                null, null, null, scope, null, 1, interval, interval));
            // Deliberately unsupported to prove that the validator withholds it.
            facts.Add(new(FactKind.MaintenanceInterval, subject, "Demo interval", "99999", "km",
                null, null, null, scope, null, 1, interval, interval));
        }
        var procedure = Find("Procedure:");
        if (procedure > 0)
            facts.Add(new(FactKind.ServiceProcedure, subject, "Procedure", "Exercise note only", null,
                null, null, null, scope, null, 1, procedure, procedure));
        var partNumber = Find("Part number:");
        if (partNumber > 0)
        {
            facts.Add(new(FactKind.AftermarketPartNumber, partName, "Part number", part, null, manufacturer, part,
                null, null, null, 1, partNumber, partNumber));
            var supersession = Find("Cross-reference:");
            facts.Add(new(FactKind.Supersession, partName, "Cross-reference", $"{part} superseded by 00-SYNTH-NEW",
                null, manufacturer, part, "00-SYNTH-NEW", null, null, 1, supersession, supersession));
            var fitment = Find("Fits HA4");
            fitments.Add(new
            {
                chassisCode = "HA4", kind = "Part", title = partName, manufacturer, category = "Cabin",
                partNumber = part, oemReference = "00-SYNTH-OE", firstLine = 1, lastLine = fitment, fitmentLine = fitment
            });
        }
        var japanese = Find("整備間隔:");
        if (japanese > 0)
            facts.Add(new(FactKind.MaintenanceInterval, "架空の整備例", "整備間隔", "24000", "km", null, null, null,
                "HA4 架空の仕様", "Synthetic maintenance interval: 24000 km.", 1, japanese, japanese));

        return Task.FromResult(new KnowledgeResponse(JsonSerializer.Serialize(new { facts, fitments }, CatalogImport.JsonOptions)));

        int Find(string marker)
        {
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Split('|').Any(field => field.TrimStart().StartsWith(marker, StringComparison.Ordinal)))
                    return i + 1;
            return 0;
        }
    }
}
