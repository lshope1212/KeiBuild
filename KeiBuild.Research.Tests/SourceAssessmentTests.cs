using System.Net;
using System.Net.Http.Headers;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;

namespace KeiBuild.Research.Tests;

public sealed class SourceAssessmentTests
{
    [Theory]
    [InlineData("https://ja.wikipedia.org/wiki/Honda_Acty", true)]
    [InlineData("https://en.wikipedia.org/wiki/File%3AHonda.jpg", false)]
    [InlineData("https://en.wikipedia.org/wiki/Page?oldid=123", false)]
    [InlineData("https://en.wikipedia.org.evil.example/wiki/Honda_Acty", false)]
    public void ArticleRuleIsScoped(string url, bool expected) =>
        Assert.Equal(expected, SourceEligibility.IsArticle(new Uri(url)));

    [Fact]
    public void PublisherDenialOverridesAutomaticExactPolicy()
    {
        var url = "https://en.wikipedia.org/wiki/Honda_Acty";
        var deny = new SourcePolicy { Key = "operator", Scope = "https://en.wikipedia.org/", Prefix = true };
        var auto = new SourcePolicy { Key = "auto-wiki-test", Scope = url, AllowFetch = true, AllowAi = true, AllowEvidence = true };
        Assert.Null(PolicyRules.Permitted([auto, deny], ResearchUnitTests.Resource(url)));
    }

    [Fact]
    public void RequiresUnambiguousRightsAndFormatForCurrentDocument()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("test") };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        response.Headers.TryAddWithoutValidation("TDM-Reservation", "0");
        Assert.Null(PublisherHeaders.OpenFormat(response));
        response.Headers.TryAddWithoutValidation("Link", "<https://creativecommons.org/publicdomain/zero/1.0/>; rel=\"license\"");
        Assert.Equal(DocumentFormat.Html, PublisherHeaders.OpenFormat(response));
        response.Headers.TryAddWithoutValidation("Link", "<https://example.org/other>; rel=license");
        Assert.Null(PublisherHeaders.OpenFormat(response));
        response.Headers.TryAddWithoutValidation("X-Robots-Tag", "noai");
        Assert.True(Assert.Throws<IngestionException>(() => PublisherHeaders.OpenFormat(response)).RequiresSourceReview);
    }

    [Fact]
    public void MissingArticleLicenseOrPageSpecificRestrictionStopsProcessing()
    {
        var url = new Uri("https://en.wikipedia.org/wiki/Test");
        var policy = new SourcePolicy { Format = DocumentFormat.Html };
        foreach (var html in new[] { "<p>public</p>", "<meta name='robots' content='noarchive'><p>public</p>" })
            Assert.True(Assert.Throws<IngestionException>(() => SourceEligibility.CheckDocument(ResearchUnitTests.Document(html), policy, url)).RequiresSourceReview);
    }

    [Fact]
    public void JapaneseChassisAssignmentsBindOnlyTheStatedConfiguration()
    {
        const string passage = "型式はトラックの2WDが「AA1」、同じくトラックの4WDが「AA2」である。";
        var doc = new NormalizedDocument([passage]);
        var draft = new FactDraft(FactKind.VehicleSpecification, "トラックの4WD", "型式", "AA2", null, null, null,
            null, null, null, 1, 1, 1);
        Assert.Equal(passage, FactEvidence.Validate(draft, doc, "AA2")?.Passage);
        Assert.Null(FactEvidence.Validate(draft with { Subject = "トラックの2WD" }, doc, "AA2"));
        Assert.Null(FactEvidence.Validate(draft, new([passage.Replace("である", "ではない")]), "AA2"));
    }
}

