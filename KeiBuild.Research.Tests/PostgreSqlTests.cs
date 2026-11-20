using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Execution;
using KeiBuild.Research.Extraction;
using KeiBuild.Research.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KEIBUILD_RESEARCH_TEST_DB")))
            Skip = "Set KEIBUILD_RESEARCH_TEST_DB to a disposable PostgreSQL database.";
    }
}

public sealed class PostgreSqlTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly RunLimits Limits = new(Searches: 14, Pages: 8, AiCalls: 8, Concurrency: 2);

    [PostgreSqlFact]
    public async Task CodexDiscoveryQuotaAndInterruptionPreservePendingQuery()
    {
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        var campaign = await store.StartAsync("pause-" + Guid.NewGuid().ToString("N"), CampaignMode.Live, Ct);
        var limits = Limits with { Searches = 1, BudgetUsd = 0 };
        using var shutdown = new CancellationTokenSource();
        var interrupted = false;
        var provider = new CodexDiscoveryProvider(new CodexCli((info, _, _) =>
        {
            if (info.ArgumentList.First() == "login") return Task.FromResult(new CodexCli.CodexExit(0, "Logged in using ChatGPT", ""));
            if (interrupted)
            {
                shutdown.Cancel();
                shutdown.Token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new CodexCli.CodexExit(1, "", "usage limit reached"));
        }));
        var runner = new CampaignRunner(db, store, provider, null, null);
        var paused = await runner.RunAsync(campaign, "discover", limits, true, false, Ct);
        Assert.Contains("usage limit", paused.StopReason);
        var attempt = await db.SearchAttempts.SingleAsync(a => a.RunId == paused.Id, Ct);
        var query = await db.Queries.SingleAsync(q => q.Id == attempt.QueryId, Ct);
        Assert.Equal(WorkState.Pending, query.State);
        Assert.Equal(0, query.Attempts);
        Assert.True(campaign.SearchAfter > DateTimeOffset.UtcNow);
        interrupted = true;
        campaign.SearchAfter = null;
        await db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(campaign, "resume", limits, true, false, shutdown.Token));
        await store.RecoverAsync(campaign.Id, limits.Retries, Ct);
        Assert.Equal(WorkState.Pending, query.State);
        Assert.Equal(0, query.Attempts);
        Assert.True(await db.SearchAttempts.AnyAsync(a => a.QueryId == query.Id && a.Error!.Contains("Interrupted"), Ct));
    }

    [PostgreSqlFact]
    public async Task VerifiedCodexLeadsEnterPolicyCollectionAndEvidencePipeline()
    {
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        var campaign = await store.StartAsync("codex-" + Guid.NewGuid().ToString("N"), CampaignMode.Live, Ct);
        await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy with { SitemapUrl = null, ExploreLinks = false }], Ct);
        var provider = CodexDiscoveryTests.Mock(CodexDiscoveryTests.Event(
            ("turn0search0", FakeResearch.Scope + "manual", "HA4 technical manual"),
            ("turn0search1", "https://unapproved.example/ha4", "HA4 manual")), new
        {
            leads = new[] { CodexDiscoveryTests.Lead("turn0search0"), CodexDiscoveryTests.Lead("turn0search1") },
            proposals = new[] { new { text = "HA4 純正部品 品番 ブレーキ", language = "ja", category = "brakes", reason = "Missing OEM brake identifiers." } }
        });
        var fake = new FakeResearch();
        var runner = new CampaignRunner(db, store, provider, fake, fake);
        var limits = Limits with { Searches = 1, Pages = 1, AiCalls = 1, BudgetUsd = .10m, ExpansionLimit = 1 };
        var run = await runner.RunAsync(campaign, "run", limits, true, true, Ct);
        Assert.Equal("codex", run.DiscoveryProvider);
        Assert.Equal(17, run.DiscoveryInputTokens);
        Assert.Equal(0, run.InputTokens);
        Assert.Equal(1, run.AiCalls);
        var saved = await db.SearchAttempts.SingleAsync(a => a.RunId == run.Id, Ct);
        Assert.Contains("turn0search0", saved.CitationsJson);
        Assert.True(await db.Jobs.AnyAsync(j => j.State == WorkState.Blocked && j.Resource.CampaignId == campaign.Id, Ct));
        Assert.True(await db.Evidence.AnyAsync(e => db.Resources.Any(r => r.CampaignId == campaign.Id &&
            r.Id == e.Version.ResourceId && r.Url == FakeResearch.Scope + "manual") && e.Passage.Contains("21 mm"), Ct));
        await GapDiscovery.ExpandAsync(db, campaign.Id, "HA4", 1, Ct);
        Assert.Equal(1, await db.Queries.CountAsync(q => q.CampaignId == campaign.Id && q.GapReason != null, Ct));
        Assert.True(await db.Queries.AnyAsync(q => q.CampaignId == campaign.Id && q.DiscoveryProvider == "codex" &&
            q.Language == "ja" && q.GapReason == "Missing OEM brake identifiers.", Ct));
    }

    [PostgreSqlFact]
    public async Task MigrationsAndOfflineCampaignPersistProvenanceDuplicatesAndConflictsAcrossSessions()
    {
        var fake = new FakeResearch();
        var name = "test-offline-" + Guid.NewGuid().ToString("N");
        Guid campaignId;
        await using (var db = Create())
        {
            await db.Database.MigrateAsync(Ct);
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Ct), id => id.EndsWith("_InitialResearch"));
            var store = new CampaignStore(db);
            await using var lease = await store.LockAsync(Ct);
            var campaign = await store.StartAsync(name, CampaignMode.Offline, Ct);
            campaignId = campaign.Id;
            await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy], Ct);
            var run = await new CampaignRunner(db, store, fake, fake, fake)
                .RunAsync(campaign, "test", Limits, true, true, Ct);
            Assert.Equal(14, run.Searches);
            Assert.True(run.DuplicateUrls > 0);
            Assert.True(run.DuplicateDocuments > 0);
            Assert.True(run.DuplicateClaims > 0);
            Assert.Equal(1, run.WithheldClaims);
            Assert.Equal(0m, run.EstimatedUsd);
        }
        await using (var db = Create())
        {
            var store = new CampaignStore(db);
            await using var lease = await store.LockAsync(Ct);
            var campaign = await store.FindAsync(name, Ct);
            Assert.Equal(campaignId, campaign.Id);
            Assert.Equal(7, await db.Topics.CountAsync(row => row.CampaignId == campaignId, Ct));
            var claimRows = await db.Claims.Where(row => row.CampaignId == campaignId).ToListAsync(Ct);
            Assert.Equal(8, claimRows.Count);
            Assert.Equal(2, claimRows.Count(row => row.Conflict));
            Assert.All(claimRows, row => Assert.Equal(Decision.Pending, row.State));
            Assert.Contains(claimRows, row => row.Kind == FactKind.Supersession && row.RelatedPartNumber == "00-SYNTH-NEW");
            Assert.DoesNotContain(claimRows, row => row.Value == "99999");
            var clearance = Assert.Single(claimRows, row => row.Value == "21");
            Assert.Equal(2, await db.Evidence.CountAsync(row => row.ClaimId == clearance.Id, Ct));
            var versions = db.Versions.Where(v => db.Resources.Any(r => r.CampaignId == campaignId && r.Id == v.ResourceId));
            Assert.True(await versions.AnyAsync(row => row.Language == "ja", Ct));
            Assert.False(await versions.AnyAsync(row => row.Snapshot != null, Ct));
            Assert.Equal(1, await db.Jobs.CountAsync(row => row.Resource.CampaignId == campaignId && row.State == WorkState.Blocked, Ct));
            var resumed = await new CampaignRunner(db, store, fake, fake, fake)
                .RunAsync(campaign, "resume", Limits with { Searches = 0 }, false, true, Ct);
            Assert.Equal(0, resumed.Pages);
            Assert.Equal(0, resumed.AiCalls);
            Assert.Equal(8, await db.Claims.CountAsync(row => row.CampaignId == campaignId, Ct));
            await Assert.ThrowsAsync<IngestionException>(() => store.ExportAsync(campaign, "unused.json", 0, Ct));
        }
    }

    [PostgreSqlFact]
    public async Task PublisherHeadersPromoteOnlyTheAssessedUrlAndDenialSurvivesResume()
    {
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        var name = "policy-" + Guid.NewGuid().ToString("N");
        var campaign = await store.StartAsync(name, CampaignMode.Live, Ct);
        var resource = new DiscoveredResource { CampaignId = campaign.Id, Url = "https://technical.publisher.org/ha4",
            Host = "technical.publisher.org", Kind = ResourceKind.Technical, Relevance = 85, Assessment = PublisherHeaders.Missing };
        db.Jobs.Add(new CrawlJob { Resource = resource, State = WorkState.Blocked });
        await db.SaveChangesAsync(Ct);
        using var client = new HttpClient(new HeaderPublisher());
        var state = Directory.CreateTempSubdirectory("keibuild-policy-test-");
        try
        {
            var http = new KeiBuild.Research.Collection.ResearchHttpCollector(new(client, state.FullName));
            var runner = new CampaignRunner(db, store, null, http, null);
            var run = await runner.RunAsync(campaign, "test", Limits with { Pages = 1 }, false, true, Ct);
            Assert.Equal(1, run.Pages);
            var policy = await db.Policies.SingleAsync(p => p.CampaignId == campaign.Id, Ct);
            Assert.Equal(resource.Url, policy.Scope);
            Assert.False(policy.Prefix);
            Assert.False(policy.AllowSnapshot);
            Assert.Equal(WorkState.Pending, (await db.Jobs.SingleAsync(j => j.ResourceId == resource.Id, Ct)).State);
            await store.ApplyPoliciesAsync(campaign, [ResearchUnitTests.Allowed("https://technical.publisher.org/") with { AllowAi = false }], Ct);
            Assert.Equal(WorkState.Blocked, (await db.Jobs.SingleAsync(j => j.ResourceId == resource.Id, Ct)).State);
        }
        finally { state.Delete(true); }
    }

    private sealed class HeaderPublisher : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/robots.txt")
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("") };
            response.Headers.TryAddWithoutValidation("TDM-Reservation", "0");
            response.Headers.TryAddWithoutValidation("Link", "<https://creativecommons.org/publicdomain/zero/1.0/>; rel=license");
            return Task.FromResult(response);
        }
    }

    [PostgreSqlFact]
    public async Task InterruptedWorkResumesAndProviderErrorsRequeueWithBackoff()
    {
        var fake = new FakeResearch();
        var name = "test-recovery-" + Guid.NewGuid().ToString("N");
        var policy = FakeResearch.Policy with { Prefix = false, Scope = FakeResearch.Scope + "parts", AllowSnapshot = true, SitemapUrl = null };
        await using (var db = Create())
        {
            await db.Database.MigrateAsync(Ct);
            var store = new CampaignStore(db);
            await using var lease = await store.LockAsync(Ct);
            var campaign = await store.StartAsync(name, CampaignMode.Offline, Ct);
            await store.ApplyPoliciesAsync(campaign, [policy], Ct);
            var runner = new CampaignRunner(db, store, new BrokenSearch(), fake, new StoppedExtractor());
            var search = await runner.RunAsync(campaign, "discover", Limits, true, false, Ct);
            Assert.Equal(1, search.Searches);
            var pending = await db.Queries.SingleAsync(row => row.CampaignId == campaign.Id && row.Attempts == 1, Ct);
            Assert.Equal(WorkState.Pending, pending.State);
            Assert.True(pending.DueAt > DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<OperationCanceledException>(() => runner.RunAsync(campaign, "work", Limits, false, true, Ct));
        }
        await using (var db = Create())
        {
            var store = new CampaignStore(db);
            await using var lease = await store.LockAsync(Ct);
            var campaign = await store.FindAsync(name, Ct);
            var deferred = new CampaignRunner(db, store, fake, new UnexpectedFetch(), new BrokenAi());
            await deferred.RunAsync(campaign, "work", Limits, false, true, Ct);
            var job = await db.Jobs.SingleAsync(row => row.Resource.CampaignId == campaign.Id, Ct);
            Assert.Equal(WorkState.Pending, job.State);
            Assert.True(job.DueAt > DateTimeOffset.UtcNow);
            Assert.True(await db.CrawlAttempts.AnyAsync(row => row.JobId == job.Id && row.Error!.Contains("Interrupted"), Ct));
            var held = await deferred.RunAsync(campaign, "work", Limits, false, true, Ct);
            Assert.Equal(0, held.Pages);
            campaign.ExtractionAfter = DateTimeOffset.UtcNow.AddSeconds(-1);
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(Ct);
            await new CampaignRunner(db, store, fake, new UnexpectedFetch(), fake).RunAsync(campaign, "resume", Limits, false, true, Ct);
            Assert.Equal(WorkState.Done, job.State);
            Assert.Equal(3, await db.Claims.CountAsync(row => row.CampaignId == campaign.Id, Ct));
        }
    }

    [PostgreSqlFact]
    public async Task ManuallyApprovedFitmentExportsThroughExistingImportContract()
    {
        var fake = new FakeResearch();
        var url = $"https://fixture-{Guid.NewGuid():N}.example/parts";
        var name = "test-approval-" + Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), name + ".json");
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        // Exercise the live state machine with wholly synthetic injected providers in this test database.
        var campaign = await store.StartAsync(name, CampaignMode.Live, Ct);
        await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy with
        {
            Prefix = false, Scope = url, SitemapUrl = null
        }], Ct);
        var run = await new CampaignRunner(db, store, fake, new PartsCollector(fake), fake)
            .RunAsync(campaign, "work", Limits, false, true, Ct);
        Assert.Equal(1, run.AiCalls);
        Assert.Empty(await db.Approved.Where(row => row.VehicleVariantId == campaign.VehicleVariantId &&
            db.Claims.Any(claim => claim.Id == row.ClaimId && claim.CampaignId == campaign.Id)).ToListAsync(Ct));
        var fitment = await db.Claims.SingleAsync(row => row.CampaignId == campaign.Id && row.Kind == FactKind.Fitment, Ct);
        await store.ReviewAsync(campaign, fitment.Id, Decision.Approved, "Verified against the synthetic test fixture.", Ct);
        try
        {
            Assert.Equal(1, await store.ExportAsync(campaign, path, 0, Ct));
            var entry = Assert.Single(new CatalogImport(["HA4"]).Parse(await File.ReadAllTextAsync(path, Ct)));
            Assert.Equal(url, entry.Source.Url);
            Assert.Equal("00-SYNTH-HA4", entry.PartNumber);
            Assert.True(await db.Approved.AnyAsync(row => row.ClaimId == fitment.Id && row.PartId != null, Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [PostgreSqlFact]
    public async Task ContinuousCampaignBoundsExpansionAndTerminatesWithBlockedJobs()
    {
        var fake = new FakeResearch();
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        await using var competing = Create();
        await Assert.ThrowsAsync<IngestionException>(() => new CampaignStore(competing).LockAsync(Ct));
        var campaign = await store.StartAsync("continuous-" + Guid.NewGuid().ToString("N"), CampaignMode.Offline, Ct);
        await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy], Ct);
        var command = CommandLine.Parse(["resume", campaign.Name, "--continuous", "--search-limit", "100",
            "--page-limit", "20", "--ai-limit", "10", "--expansion-limit", "3"]);
        var session = new CampaignSession(db, new(db, store, fake, fake, fake), fake);
        await session.RunAsync(campaign, command, true, true, Ct);
        var queries = db.Queries.Where(q => q.CampaignId == campaign.Id);
        Assert.Equal(31, await queries.CountAsync(Ct));
        Assert.Equal(3, await queries.CountAsync(q => q.OriginClaimId != null, Ct));
        Assert.All(await queries.ToListAsync(Ct), q => Assert.Equal(WorkState.Done, q.State));
        Assert.Equal(8, await db.Claims.CountAsync(c => c.CampaignId == campaign.Id && c.State == Decision.Pending, Ct));
        var before = await db.Runs.Where(r => r.CampaignId == campaign.Id).SumAsync(r => r.AiCalls, Ct);
        await session.RunAsync(campaign, command, true, true, Ct);
        Assert.Equal(before, await db.Runs.Where(r => r.CampaignId == campaign.Id).SumAsync(r => r.AiCalls, Ct));
        var report = new CampaignReport(db);
        Assert.Contains("UnresolvedGaps", System.Text.Json.JsonSerializer.Serialize(await report.CoverageAsync(campaign.Id, Ct)));
        Assert.Contains("unapproved.example", System.Text.Json.JsonSerializer.Serialize(await report.JobsAsync(campaign.Id, 0, Ct)));
    }

    [PostgreSqlFact]
    public async Task CodexProcessResultsPersistWithUsageAndEvidenceOnZeroApiBudget()
    {
        var fake = new FakeResearch();
        var url = $"https://fixture-{Guid.NewGuid():N}.example/parts";
        var extractor = new CodexKnowledgeExtractor(async (info, stdin, ct) =>
        {
            if (info.ArgumentList.First() == "login") return new(0, "", "Logged in using ChatGPT");
            Assert.Contains(url, stdin);
            var resource = ResearchUnitTests.Resource(FakeResearch.Scope + "parts");
            var doc = await fake.FetchAsync(resource, new(), 524288, ct);
            var normalized = KeiBuild.Ingestion.Parsing.DocumentNormalizer.Normalize(doc, KeiBuild.Ingestion.Sources.DocumentFormat.Html);
            var reply = await fake.ExtractAsync(new(normalized, ["HA4"], "en"), ct);
            return CodexExtractionTests.Success(reply.Json);
        });
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        var campaign = await store.StartAsync("codex-" + Guid.NewGuid().ToString("N"), CampaignMode.Live, Ct);
        await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy with { Prefix = false,
            Scope = url, SitemapUrl = null }], Ct);
        var session = new CampaignSession(db, new(db, store, null, new PartsCollector(fake), extractor), extractor);
        var command = CommandLine.Parse(["work", campaign.Name, "--continuous", "--budget-usd", "0", "--ai-limit", "1"]);
        await session.RunAsync(campaign, command, false, true, Ct);
        var run = await db.Runs.SingleAsync(r => r.CampaignId == campaign.Id && r.AiCalls == 1, Ct);
        Assert.Equal("Codex/ChatGPT", run.Provider);
        Assert.Equal(0m, run.EstimatedUsd);
        Assert.Equal(17, run.InputTokens);
        Assert.Equal(3, await db.Claims.CountAsync(c => c.CampaignId == campaign.Id && c.State == Decision.Pending, Ct));
        Assert.Equal(3, await db.Evidence.CountAsync(e => db.Claims.Any(c => c.Id == e.ClaimId && c.CampaignId == campaign.Id), Ct));
    }

    [PostgreSqlFact]
    public async Task StopWhileWaitingForDueWorkReleasesSessionAndResumeProcessesTheQueue()
    {
        await using var db = Create();
        await db.Database.MigrateAsync(Ct);
        var store = new CampaignStore(db);
        await using var lease = await store.LockAsync(Ct);
        var campaign = await store.StartAsync("stop-" + Guid.NewGuid().ToString("N"), CampaignMode.Offline, Ct);
        var fake = new FakeResearch();
        var command = CommandLine.Parse(["discover", campaign.Name, "--continuous", "--search-limit", "1"]);
        var future = DateTimeOffset.UtcNow.AddHours(1);
        await db.Queries.Where(q => q.CampaignId == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(q => q.DueAt, future), Ct);
        var session = new CampaignSession(db, new(db, store, fake, null, null), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = session.RunAsync(campaign, command, true, false, timeout.Token);
        await using var monitor = Create();
        while (!await monitor.Runs.AnyAsync(r => r.CampaignId == campaign.Id && r.EndedAt != null, timeout.Token))
            await Task.Delay(20, timeout.Token);
        await monitor.Campaigns.Where(c => c.Id == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.StopRequested, true), Ct);
        await running;
        Assert.Equal(0, await monitor.SearchAttempts.CountAsync(a =>
            monitor.Runs.Any(r => r.Id == a.RunId && r.CampaignId == campaign.Id), Ct));
        var now = DateTimeOffset.UtcNow;
        await monitor.Queries.Where(q => q.CampaignId == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(q => q.DueAt, now), Ct);
        await session.RunAsync(campaign, command, true, false, timeout.Token);
        Assert.Equal(1, await monitor.Queries.CountAsync(q => q.CampaignId == campaign.Id && q.State == WorkState.Done, Ct));
    }

    private sealed class UnexpectedFetch : KeiBuild.Research.Collection.IResearchCollector
    {
        public Task<KeiBuild.Ingestion.Collection.CollectedDocument> FetchAsync(DiscoveredResource resource,
            SourcePolicy policy, int maximumBytes, CancellationToken ct) =>
            throw new InvalidOperationException("Expected recovery from the permitted snapshot.");
    }

    private sealed class PartsCollector(FakeResearch fake) : KeiBuild.Research.Collection.IResearchCollector
    {
        public Task<KeiBuild.Ingestion.Collection.CollectedDocument> FetchAsync(DiscoveredResource resource,
            SourcePolicy policy, int maximumBytes, CancellationToken ct) =>
            fake.FetchAsync(ResearchUnitTests.Resource(FakeResearch.Scope + "parts"), policy, maximumBytes, ct);
    }

    private static ResearchDb Create() => new(ResearchDb.Options(
        Environment.GetEnvironmentVariable("KEIBUILD_RESEARCH_TEST_DB")!));

    private sealed class BrokenSearch : ISearchProvider
    {
        public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            throw new IngestionException("Synthetic network failure.") { IsTransient = true };
    }

    private sealed class StoppedExtractor : IKnowledgeExtractor
    {
        public string Name => "Interrupted test";
        public Task<KnowledgeResponse> ExtractAsync(ExtractionInput input, CancellationToken ct) =>
            throw new OperationCanceledException();
    }

    private sealed class BrokenAi : IKnowledgeExtractor
    {
        public string Name => "Unavailable test";
        public Task<KnowledgeResponse> ExtractAsync(ExtractionInput input, CancellationToken ct) =>
            throw new IngestionException("Synthetic model rate limit.") { IsTransient = true, HttpStatus = 429 };
    }
}
