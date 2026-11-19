using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Ingestion.Pipeline;
using KeiBuild.Ingestion.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace KeiBuild.Ingestion.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task OfflineFixtureExportsThenQueuesAndPublishesOnlyAfterApproval()
    {
        using var temp = new TestDirectory();
        var source = await Fixture.SourceAsync();
        var document = await LoadFixture(source);
        var fake = new FileClaimExtractor(Fixture.PathFor("synthetic-response.json"));
        var result = await new IngestionPipeline(Fixture.ChassisCodes, fake).RunAsync(source, document, Fixture.Ct);
        var path = await RunExport.WriteAsync(temp.PathFor("run"), result, document, saveSnapshot: false, Fixture.Ct);
        var payload = await File.ReadAllTextAsync(path, Fixture.Ct);

        var entry = Assert.Single(new CatalogImport(Fixture.ChassisCodes).Parse(payload));
        Assert.Equal("HA4", entry.ChassisCode);
        Assert.Equal("00-FIXTURE-12", entry.PartNumber);
        Assert.Equal("00-FIXTURE-OE", entry.OemReference);
        Assert.Equal(source.Url, entry.Source.Url);
        Assert.Equal(source.Title, entry.Source.Title);
        Assert.Equal(source.Kind, entry.Source.Kind);
        Assert.Contains("line 4", entry.Source.Locator);
        Assert.Contains("Fictional 1998 manual configuration only; excludes automatic", entry.Conditions);
        Assert.DoesNotContain("Unrelated navigation", entry.Source.Evidence);
        Assert.Equal(source.Publisher, result.Provenance.Source.Publisher);
        Assert.Equal("en", result.Provenance.Source.Language);
        Assert.Equal(document.CollectedAtUtc, result.Provenance.CollectedAtUtc);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(document.Bytes)), result.Provenance.Sha256);
        Assert.Null(result.Provenance.RetrievedAtUtc);
        Assert.True(File.Exists(temp.PathFor("run/provenance.json")));
        Assert.False(File.Exists(temp.PathFor("run/source.html")));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["KeiBuild:ReviewFile"] = temp.PathFor("review.json") }).Build();
        var environment = new CatalogEnvironment();
        var store = new CatalogStore(environment, configuration);
        Assert.Equal((1, 0), store.Import(payload));
        Assert.Equal((0, 1), store.Import(payload));
        Assert.DoesNotContain(store.PublishedEntries(), item => item.PartNumber == entry.PartNumber);
        var item = Assert.Single(store.ReviewItems());
        Assert.Equal(ReviewStatus.Pending, item.Status);
        Assert.True(store.Review(item.Id, ReviewStatus.Approved));
        var restarted = new CatalogStore(environment, configuration);
        Assert.Contains(restarted.PublishedEntries(), item => item == entry);
    }

    [Theory]
    [InlineData("title", null, "title")]
    [InlineData("manufacturer", "", "manufacturer")]
    [InlineData("chassisCode", "ZZ99", "chassis code")]
    [InlineData("chassisCode", "HA3", "does not explicitly support")]
    [InlineData("partNumber", null, "part number")]
    [InlineData("partNumber", "0-FIXTURE-12", "exact part number")]
    [InlineData("partNumber", "00-FIXTURE", "exact part number")]
    [InlineData("oemReference", "00-FIXTURE-12", "exact OEM reference")]
    [InlineData("manufacturer", "Unknown Maker", "name and manufacturer")]
    public async Task DraftsWithInvalidOrUnsupportedFieldsAreWithheld(string property, string? value, string reason)
    {
        var draft = await FixtureDraft();
        draft[property] = value;
        var result = await RunFixture(new JsonArray(draft));

        Assert.Equal("[]", result.CandidateJson);
        Assert.Contains(reason, Assert.Single(result.Provenance.Withheld).Reason);
    }

    [Fact]
    public async Task OutOfRangeEvidenceIsWithheld()
    {
        var draft = await FixtureDraft();
        draft["lastLine"] = 90;
        var result = await RunFixture(new JsonArray(draft));
        Assert.Contains("outside the document", Assert.Single(result.Provenance.Withheld).Reason);
    }

    [Fact]
    public async Task IdenticalDraftsAreDeduplicated()
    {
        var draft = await FixtureDraft();
        var result = await RunFixture(new JsonArray(draft.DeepClone(), draft));
        Assert.Equal(1, result.Provenance.Accepted);
        Assert.Equal(1, result.Provenance.Duplicates);
    }

    [Fact]
    public async Task EmptyExtractionIsAValidNoOp()
    {
        var result = await RunFixture(new JsonArray());
        Assert.Equal("[]", result.CandidateJson);
        Assert.Equal(0, result.Provenance.Accepted);
        Assert.Empty(result.Provenance.Withheld);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"claims\":[{\"sourceUrl\":\"https://false.example/\"}]}")]
    [InlineData("{\"claims\":[{\"kind\":\"Unknown\"}]}")]
    public async Task MalformedOrOutOfSchemaProviderResponseFails(string json)
    {
        var source = await Fixture.SourceAsync();
        var exception = await Assert.ThrowsAsync<IngestionException>(() => new IngestionPipeline(
            Fixture.ChassisCodes, new FakeExtractor(json)).RunAsync(source, new(Encoding.UTF8.GetBytes("<p>Fixture</p>"), DateTimeOffset.UtcNow), Fixture.Ct));
        Assert.Contains("extractor JSON", exception.Message);
    }

    [Theory]
    [InlineData("Fitment: HA40", "does not explicitly support")]
    [InlineData("Chassis: HA4 (unsupported)", "does not explicitly support")]
    [InlineData("Does not fit HA4", "does not explicitly support")]
    [InlineData("Fits HA3 except HA4", "does not explicitly support")]
    [InlineData("Fitment problems on HA4", "does not explicitly support")]
    [InlineData("Application: HA4 | Part number: 00-OTHER-01", "exact part number")]
    public async Task NegativeMentionOrDifferentApplicationIsWithheld(string fitment, string reason)
    {
        var draft = await FixtureDraft();
        draft["firstLine"] = 1;
        draft["lastLine"] = 4;
        draft["fitmentLine"] = 4;
        var source = (await Fixture.SourceAsync()) with { Format = DocumentFormat.Text };
        var document = TextDocument(fitment);
        var result = await Run(source, document, new JsonArray(draft));
        Assert.Equal(0, result.Provenance.Accepted);
        Assert.Contains(reason, Assert.Single(result.Provenance.Withheld).Reason);
    }

    [Fact]
    public async Task IndependentlyCitedApplicationStatementsAreKept()
    {
        var draft = await FixtureDraft();
        var second = draft.DeepClone().AsObject();
        second["lastLine"] = 5;
        second["fitmentLine"] = 5;
        var source = (await Fixture.SourceAsync()) with { Format = DocumentFormat.Text };
        var document = TextDocument("Fitment: HA4; 1998 manual only.\nFitment: HA4; 1998 automatic only.");
        var result = await Run(source, document, new JsonArray(draft, second));
        Assert.Equal(2, result.Provenance.Accepted);
        var entries = new CatalogImport(Fixture.ChassisCodes).Parse(result.CandidateJson);
        Assert.Contains("manual only", entries[0].Conditions);
        Assert.Contains("automatic only", entries[1].Conditions);
    }

    [Fact]
    public void HtmlAndMarkdownPreserveApplicationColumns()
    {
        var text = """
            | 車種型式 | 品番 | 相当純正部番 |
            | --- | --- | --- |
            | HA4 | 00-DEMO-A | 00-DEMO-OE |
            """;
        var doc = new CollectedDocument(Encoding.UTF8.GetBytes(text), DateTimeOffset.UtcNow);
        var normalized = DocumentNormalizer.Normalize(doc, DocumentFormat.Markdown);
        Assert.Contains("車種型式: HA4 | 品番: 00-DEMO-A | 相当純正部番: 00-DEMO-OE", normalized.Lines);
        Assert.Contains("00-DEMO-A", DocumentNormalizer.Normalize(doc, DocumentFormat.Text).Lines.Last());
    }

    [Fact]
    public void NormalizationBoundsTheExpandedTextFromTables()
    {
        var html = $"<table><tr><th>{new string('x', 14_000)}</th></tr><tr><td>HA4</td></tr><tr><td>HA3</td></tr></table>";
        var doc = new CollectedDocument(Encoding.UTF8.GetBytes(html), DateTimeOffset.UtcNow);
        var exception = Assert.Throws<IngestionException>(() => DocumentNormalizer.Normalize(doc, DocumentFormat.Html));
        Assert.Contains("40,000 characters", exception.Message);
    }

    [Fact]
    public async Task ExplicitlyAllowedSnapshotRetainsOriginalBytesAndExistingExportIsProtected()
    {
        using var temp = new TestDirectory();
        var source = await Fixture.SourceAsync();
        var document = await LoadFixture(source);
        var result = await Run(source, document, new JsonArray(await FixtureDraft()));
        var target = temp.PathFor("export");
        await RunExport.WriteAsync(target, result, document, saveSnapshot: true, Fixture.Ct);
        Assert.Equal(document.Bytes, await File.ReadAllBytesAsync(temp.PathFor("export/source.html"), Fixture.Ct));
        await Assert.ThrowsAsync<IngestionException>(() => RunExport.WriteAsync(target, result, document, false, Fixture.Ct));

        var disallowed = result with { Provenance = result.Provenance with { Source = source with { SnapshotAllowed = false } } };
        await Assert.ThrowsAsync<IngestionException>(() => RunExport.WriteAsync(temp.PathFor("blocked"), disallowed, document, true, Fixture.Ct));
        Assert.False(Directory.Exists(temp.PathFor("blocked")));
    }

    [Fact]
    public async Task FileFailuresAreReportedBeforeCallingTheModel()
    {
        using var temp = new TestDirectory();
        var source = await Fixture.SourceAsync();
        await Assert.ThrowsAsync<IngestionException>(() => DocumentBytes.CollectFileAsync(temp.PathFor("missing.html"), source, Fixture.Ct));
        await Assert.ThrowsAsync<IngestionException>(() => DocumentBytes.CollectFileAsync(temp.PathFor("wrong.pdf"), source, Fixture.Ct));
        var huge = temp.PathFor("too-large.html");
        await File.WriteAllBytesAsync(huge, new byte[DocumentBytes.MaxBytes + 1], Fixture.Ct);
        await Assert.ThrowsAsync<IngestionException>(() => DocumentBytes.CollectFileAsync(huge, source, Fixture.Ct));
        Assert.Throws<IngestionException>(() => DocumentBytes.Decode([0xC3, 0x28], "utf-8"));
    }

    private static CollectedDocument TextDocument(string application) => new(Encoding.UTF8.GetBytes(
        "SYNTHETIC — Demo cabin filter\nManufacturer: Fixture Parts Co.\nPart number: 00-FIXTURE-12 | OEM reference: 00-FIXTURE-OE\n" +
        application), DateTimeOffset.UtcNow);

    private static async Task<JsonObject> FixtureDraft()
    {
        var model = await File.ReadAllTextAsync(Fixture.PathFor("synthetic-response.json"), Fixture.Ct);
        return JsonNode.Parse(model)!["claims"]![0]!.DeepClone().AsObject();
    }

    private static Task<CollectedDocument> LoadFixture(SourceDefinition source) =>
        DocumentBytes.CollectFileAsync(Fixture.PathFor("synthetic-fitment.html"), source, Fixture.Ct);

    private static async Task<IngestionResult> RunFixture(JsonArray drafts)
    {
        var source = await Fixture.SourceAsync();
        return await Run(source, await LoadFixture(source), drafts);
    }

    private static Task<IngestionResult> Run(SourceDefinition source, CollectedDocument document, JsonArray drafts) =>
        new IngestionPipeline(Fixture.ChassisCodes, new FakeExtractor(new JsonObject { ["claims"] = drafts }.ToJsonString()))
            .RunAsync(source, document, Fixture.Ct);

    private sealed class FakeExtractor(string response) : IClaimExtractor
    {
        public string Name => "Test fake";
        public Task<string> ExtractAsync(ExtractionInput input, CancellationToken ct) => Task.FromResult(response);
    }

    private sealed class CatalogEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "KeiBuild";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
