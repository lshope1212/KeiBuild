using System.Net;
using System.Text;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Tests;

public sealed class CollectionTests
{
    private const string ApprovedUrl = "https://catalog.example.com/parts/one";

    [Theory]
    [InlineData("http://catalog.example.com/parts/one")]
    [InlineData("https://127.0.0.1/parts")]
    [InlineData("https://localhost/parts")]
    [InlineData("https://localhost.local/parts")]
    [InlineData("https://catalog.example.com:8443/parts")]
    [InlineData("https://catalog.example.com/parts#private")]
    public async Task UnsafeCollectionUrlsAreRejectedBeforeAnyNetworkActivity(string url)
    {
        using var temp = new TestDirectory();
        using var handler = new StubHttp(_ => throw new InvalidOperationException("No request expected."));
        using var client = new HttpClient(handler);
        var source = await ApprovedSource();
        var exception = await Assert.ThrowsAsync<IngestionException>(() =>
            new HttpDocumentCollector(client, temp.Root).CollectAsync(source with { Url = url }, Fixture.Ct));
        Assert.Contains("public HTTPS", exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnapprovedSourceAndInvalidClassificationAreRejected()
    {
        using var temp = new TestDirectory();
        using var handler = new StubHttp(_ => throw new InvalidOperationException("No request expected."));
        using var client = new HttpClient(handler);
        var source = await ApprovedSource();
        var collector = new HttpDocumentCollector(client, temp.Root);
        var unapproved = await Assert.ThrowsAsync<IngestionException>(() =>
            collector.CollectAsync(source with { AutomatedRetrievalApproved = false }, Fixture.Ct));
        Assert.Contains("not approved", unapproved.Message);
        var invalid = await Assert.ThrowsAsync<IngestionException>(() =>
            collector.CollectAsync(source with { Kind = (SourceKind)123 }, Fixture.Ct));
        Assert.Contains("source type", invalid.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CollectorRequestsOnlyRobotsAndTheExactApprovedUrlAndAppliesCooldown()
    {
        using var temp = new TestDirectory();
        using var handler = new StubHttp(uri => uri.AbsolutePath == "/robots.txt" ?
            Text(HttpStatusCode.OK, "User-agent: *\nDisallow: /other/") :
            Text(HttpStatusCode.OK, "Permitted fixture document"));
        using var client = new HttpClient(handler);
        var source = await ApprovedSource();
        var collector = new HttpDocumentCollector(client, temp.Root);
        var result = await collector.CollectAsync(source, Fixture.Ct);
        Assert.Equal("Permitted fixture document", Encoding.UTF8.GetString(result.Bytes));
        Assert.True(result.RetrievedFromUrl);
        Assert.Equal(new[] { "https://catalog.example.com/robots.txt", ApprovedUrl },
            handler.Requests.Select(uri => uri.AbsoluteUri));
        await Assert.ThrowsAsync<IngestionException>(() => collector.CollectAsync(source, Fixture.Ct));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(200, "User-agent: *\nDisallow: /parts/", "disallows")]
    [InlineData(302, "", "HTTP 302")]
    [InlineData(403, "", "HTTP 403")]
    [InlineData(429, "", "HTTP 429")]
    [InlineData(200, "User-agent: *\nCrawl-delay: 31", "unsupported crawl delay")]
    public async Task RobotsRestrictionsAndFailedChecksStopTheDocumentRequest(int status, string text, string reason)
    {
        using var temp = new TestDirectory();
        using var handler = new StubHttp(_ => Text((HttpStatusCode)status, text));
        using var client = new HttpClient(handler);
        var source = await ApprovedSource();
        var exception = await Assert.ThrowsAsync<IngestionException>(() =>
            new HttpDocumentCollector(client, temp.Root).CollectAsync(source, Fixture.Ct));
        Assert.Contains(reason, exception.Message);
        Assert.Equal("/robots.txt", Assert.Single(handler.Requests).AbsolutePath);
    }

    [Fact]
    public void RobotsTargetsAgentSpecificGroupAndTheLongestMatchingRule()
    {
        var robots = """
            User-agent: *
            Disallow: /

            User-agent: KeiBuildIngestion
            Disallow: /parts/*
            Allow: /parts/one$
            Crawl-delay: 2
            """;
        Assert.Equal(TimeSpan.FromSeconds(2), RobotsRules.Check(robots, new Uri(ApprovedUrl)));
        Assert.Throws<IngestionException>(() => RobotsRules.Check(robots, new Uri(ApprovedUrl + "?another=1")));
        Assert.Throws<IngestionException>(() =>
            RobotsRules.Check("User-agent: *\nDisallow: /%70arts/", new Uri(ApprovedUrl)));
    }

    [Theory]
    [InlineData("redirect", "HTTP 302")]
    [InlineData("large", "512 KiB")]
    [InlineData("type", "unexpected content type")]
    public async Task CollectorRejectsRedirectsOversizedResponsesAndUnexpectedFormats(string failure, string reason)
    {
        using var temp = new TestDirectory();
        using var handler = new StubHttp(uri => uri.AbsolutePath == "/robots.txt" ?
            Text(HttpStatusCode.NotFound, "") : failure switch
            {
                "redirect" => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://catalog.example.com/different") }
                },
                "large" => Text(HttpStatusCode.OK, new string('x', DocumentBytes.MaxBytes + 1)),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", Encoding.UTF8, "text/html") }
            });
        using var client = new HttpClient(handler);
        var source = await ApprovedSource();
        var exception = await Assert.ThrowsAsync<IngestionException>(() =>
            new HttpDocumentCollector(client, temp.Root).CollectAsync(source, Fixture.Ct));
        Assert.Contains(reason, exception.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static async Task<SourceDefinition> ApprovedSource() => (await Fixture.SourceAsync()) with
    {
        Url = ApprovedUrl,
        AutomatedRetrievalApproved = true,
        Format = DocumentFormat.Text
    };

    private static HttpResponseMessage Text(HttpStatusCode code, string text) =>
        new(code) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    private sealed class StubHttp(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains(RobotsRules.ProductToken, request.Headers.UserAgent.ToString());
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri);
            return Task.FromResult(reply(uri));
        }
    }
}
