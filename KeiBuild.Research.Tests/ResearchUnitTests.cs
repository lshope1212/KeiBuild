using System.Text;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Collection;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Execution;
using KeiBuild.Research.Extraction;

namespace KeiBuild.Research.Tests;

public sealed class ResearchUnitTests
{
    [Fact]
    public void Ha4PlanCoversEveryTopicInBothLanguages()
    {
        var queries = Ha4Plan.Queries().ToArray();
        Assert.Equal(28, queries.Length);
        Assert.Equal(28, queries.DistinctBy(query => query.Text).Count());
        Assert.All(queries, query => Assert.Contains("HA4", query.Text));
        Assert.Contains(queries, query => query.Language == "ja" && query.Text.Contains("純正部品"));
        Assert.Contains(queries, query => query.Language == "en" && query.Text.Contains("timing belt"));
        Assert.Equal(7, queries.Take(14).GroupBy(query => query.Topic).Count());
        Assert.All(queries.GroupBy(query => query.Topic), group => Assert.Equal(2, group.Select(query => query.Language).Distinct().Count()));
    }

    [Fact]
    public void BraveParsesDeduplicatesAndKeepsQueryParameters()
    {
        var json = """
            {"web":{"results":[
              {"url":"https://Example.com:443/ha4?q=1#a","title":"<strong>HA4</strong> &amp; E07A","description":"manual"},
              {"url":"https://example.com/ha4?q=1#b","title":"Duplicate"},
              {"url":"https://example.com/ha4?q=2","title":"Different application"},
              {"url":"https://localhost/ha4"},
              {"url":"ftp://example.com/ha4"}
            ]}}
            """;
        var hits = BraveSearchProvider.ParseResults(Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, hits.Count);
        Assert.Equal("HA4 & E07A", hits[0].Title);
        Assert.Equal("https://example.com/ha4?q=1", UrlRules.Normalize(hits[0].Url)!.AbsoluteUri);
        Assert.Throws<IngestionException>(() => BraveSearchProvider.ParseResults(Encoding.UTF8.GetBytes("{")));
    }

    [Theory]
    [InlineData("https://global.honda/jp", "HA4 E07A specifications", ResourceKind.Manufacturer)]
    [InlineData("https://someforum.example/ha4", "HA4 repair discussion", ResourceKind.Community)]
    [InlineData("https://example.com/ha4", "Acty HA4 part, add to cart", ResourceKind.Vendor)]
    [InlineData("https://example.com/ha40", "HA40 specification", ResourceKind.Irrelevant)]
    [InlineData("https://example.com/資料", "アクティ HA4 部品 品番", ResourceKind.Technical)]
    public void RelevanceUsesExactChassisAndSourceSignals(string url, string title, ResourceKind kind) =>
        Assert.Equal(kind, Relevance.Classify(new Uri(url), title, "").Kind);

    [Theory]
    [InlineData("http://example.com/ha4/")]
    [InlineData("https://name:secret@example.com/ha4/")]
    [InlineData("https://127.0.0.1/ha4/")]
    [InlineData("https://localhost/ha4/")]
    [InlineData("https://example.com/ha4/%2Fprivate")]
    [InlineData("https://example.com/ha4/%252Fprivate")]
    [InlineData("https://example.com/ha4/a;private/file")]
    public void UnsupportedDiscoveryUrlsAreRejected(string url) => Assert.Null(UrlRules.Normalize(url));

    [Fact]
    public void PolicyRequiresApprovalForCollectionAiAndEvidenceAndExactDenialWins()
    {
        var allow = BuildPolicy(Allowed("https://example.com/ha4/"));
        var deny = BuildPolicy(Allowed("https://example.com/ha4/private") with
        {
            Key = "deny", Prefix = false, AllowFetch = false
        });
        Assert.NotNull(PolicyRules.Permitted([allow], Resource("https://example.com/ha4/part")));
        Assert.Null(PolicyRules.Permitted([allow, deny], Resource("https://example.com/ha4/private")));
        Assert.Null(PolicyRules.Permitted([allow], Resource("https://example.com/ha4-other/part")));
        Assert.Null(PolicyRules.Permitted([allow], Resource("https://other.example.com/ha4/part")));
        Assert.Null(PolicyRules.Permitted([BuildPolicy(Allowed(allow.Scope) with { AllowEvidence = false })],
            Resource("https://example.com/ha4/part")));
        Assert.Throws<IngestionException>(() => BuildPolicy(Allowed("https://example.com/ha4")));
    }

    [Fact]
    public void LinkDiscoveryHonorsNoFollowOriginAndSitemapRestrictions()
    {
        var origin = new Uri("https://example.com/ha4/a");
        var html = """<body><a href="/ha4/item">Part</a><a rel="ugc nofollow" href="/ha4/no">Skip</a><a href="https://other.example/ha4">Outside</a></body>""";
        Assert.Equal("https://example.com/ha4/item", Assert.Single(LinkDiscovery.FromHtml(Document(html), origin)).Url);
        Assert.Empty(LinkDiscovery.FromHtml(Document("""<meta name="robots" content="nofollow"><a href="/ha4/item">Part</a>"""), origin));
        var sitemap = """<urlset><url><loc>https://example.com/ha4/b</loc></url><url><loc>https://other.example/ha4</loc></url></urlset>""";
        Assert.Equal("https://example.com/ha4/b", Assert.Single(LinkDiscovery.FromSitemap(Document(sitemap), origin)).Url);
        Assert.Throws<IngestionException>(() => LinkDiscovery.FromSitemap(Document("<!DOCTYPE urlset><urlset/>"), origin));
    }

    [Fact]
    public void FactsRequireAnExactLabeledValueUnitAndHa4Evidence()
    {
        var document = Document("""
            <html lang="ja"><body><h1>テスト HA4</h1><p>適用: HA4 架空の仕様</p>
            <table><tr><th>間隔</th><th>別項目</th></tr><tr><td>24000 km</td><td>99999 km</td></tr></table></body></html>
            """);
        var normalized = DocumentNormalizer.Normalize(document, DocumentFormat.Html);
        Assert.Equal("ja", normalized.Language);
        var draft = new FactDraft(FactKind.MaintenanceInterval, "テスト HA4", "間隔", "24000", "km", null, null, null,
            "HA4 架空の仕様", "Synthetic translation", 1, 4, 4);
        var valid = Assert.IsType<GroundedFact>(FactEvidence.Validate(draft, normalized));
        Assert.Contains("24000 km", valid.Passage);
        Assert.Equal("Synthetic translation", valid.EnglishTranslation);
        Assert.Null(FactEvidence.Validate(draft with { Value = "99999" }, normalized));
        Assert.Null(FactEvidence.Validate(draft with { Unit = null }, normalized));
        Assert.Null(FactEvidence.Validate(draft with { Unit = "mi" }, normalized));
        Assert.Null(FactEvidence.Validate(draft with { Applicability = "HA4 2005" }, normalized));
        Assert.Null(FactEvidence.Validate(draft with { FactLine = 2 }, normalized));
    }

    [Fact]
    public async Task MalformedProviderClaimEnvelopeCannotAssignSourceAttribution()
    {
        var source = new SourceDefinition("fixture", "Synthetic", "Synthetic", "https://example.com/ha4/fixture",
            SourceKind.Manufacturer, DocumentFormat.Text, false, "Synthetic fixture", false);
        var document = Document("HA4 fixture");
        var normalized = DocumentNormalizer.Normalize(document, source.Format);
        var reply = new KnowledgeResponse("""{"facts":[],"fitments":[],"sourceUrl":"https://other.example/"}""");
        await Assert.ThrowsAsync<IngestionException>(() => KnowledgeAnalysis.ValidateAsync(reply, source, document, normalized, CancellationToken.None));
    }

    [Fact]
    public void ExactMatchingPreservesPartPunctuationAndScopesWhileFlaggingSameScopeValues()
    {
        var fact = new GroundedFact(FactKind.MechanicalSpecification, "clearance", "gap", "20", "mm",
            " Example  Parts ", "000-11", null, "HA4 manual", "Line 1", "Grounded", null);
        var group = ClaimIdentity.Group(fact);
        Assert.Equal(group, ClaimIdentity.Group(fact with { Manufacturer = "example parts" }));
        Assert.NotEqual(group, ClaimIdentity.Group(fact with { PartNumber = "00011" }));
        Assert.NotEqual(group, ClaimIdentity.Group(fact with { Applicability = "HA4 automatic" }));
        Assert.NotEqual(ClaimIdentity.Claim(fact, group), ClaimIdentity.Claim(fact with { Value = "21" }, group));
    }

    [Fact]
    public void RunBudgetReservesCostsBeforeCallsAndEnforcesHardCountsOffline()
    {
        var run = new ResearchRun();
        var limits = new RunLimits(Searches: 3, Pages: 1, AiCalls: 2, BudgetUsd: .05m);
        var budget = new RunBudget(limits, run, offline: false);
        Assert.True(budget.ReserveSearch());
        Assert.True(budget.ReserveAnalysis());
        Assert.False(budget.ReserveSearch());
        Assert.False(budget.ReserveAnalysis());
        Assert.True(budget.ReservePage());
        Assert.False(budget.ReservePage());
        Assert.Equal(.05m, run.EstimatedUsd);
        var offline = new RunBudget(limits with { BudgetUsd = 0, AiCalls = 1 }, new(), offline: true);
        Assert.True(offline.ReserveAnalysis());
        Assert.False(offline.ReserveAnalysis());
    }

    [Fact]
    public void MissingApiKeyReturnsConfigurationHelp()
    {
        var error = Assert.Throws<IngestionException>(() => ProviderKeys.Require(_ => " ", "BRAVE_SEARCH_API_KEY"));
        Assert.Contains("Set BRAVE_SEARCH_API_KEY", error.Message);
    }

    internal static CollectedDocument Document(string text) => new(Encoding.UTF8.GetBytes(text), DateTimeOffset.UtcNow, "utf-8");
    internal static DiscoveredResource Resource(string url) => new() { Url = url, Kind = ResourceKind.Technical };
    internal static PolicyInput Allowed(string scope) => new("approved", scope, true, "Test publisher",
        SourceKind.Manufacturer, DocumentFormat.Html, "Approved synthetic document in a test.", true, true, true, false, true);
    private static SourcePolicy BuildPolicy(PolicyInput input)
    {
        var policy = new SourcePolicy();
        PolicyRules.Apply(input, policy);
        return policy;
    }
}
