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
            Assert.True(await db.Versions.AnyAsync(row => row.Language == "ja", Ct));
            Assert.False(await db.Versions.AnyAsync(row => row.Snapshot != null, Ct));
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
    public async Task InterruptedWorkResumesAndProviderErrorsRequeueWithBackoff()
    {
        var fake = new FakeResearch();
        var name = "test-recovery-" + Guid.NewGuid().ToString("N");
        var policy = FakeResearch.Policy with { Prefix = false, Scope = FakeResearch.Scope + "parts", SitemapUrl = null };
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
            var deferred = new CampaignRunner(db, store, fake, fake, new BrokenAi());
            await deferred.RunAsync(campaign, "work", Limits, false, true, Ct);
            var job = await db.Jobs.SingleAsync(row => row.Resource.CampaignId == campaign.Id, Ct);
            Assert.Equal(WorkState.Pending, job.State);
            Assert.True(job.DueAt > DateTimeOffset.UtcNow);
            Assert.True(await db.CrawlAttempts.AnyAsync(row => row.JobId == job.Id && row.Error!.Contains("Interrupted"), Ct));
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync(Ct);
            await new CampaignRunner(db, store, fake, fake, fake).RunAsync(campaign, "resume", Limits, false, true, Ct);
            Assert.Equal(WorkState.Done, job.State);
            Assert.Equal(3, await db.Claims.CountAsync(row => row.CampaignId == campaign.Id, Ct));
        }
    }

    [PostgreSqlFact]
    public async Task ManuallyApprovedFitmentExportsThroughExistingImportContract()
    {
        var fake = new FakeResearch();
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
            Prefix = false, Scope = FakeResearch.Scope + "parts", SitemapUrl = null
        }], Ct);
        var run = await new CampaignRunner(db, store, fake, fake, fake)
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
            Assert.Equal(FakeResearch.Scope + "parts", entry.Source.Url);
            Assert.Equal("00-SYNTH-HA4", entry.PartNumber);
            Assert.True(await db.Approved.AnyAsync(row => row.ClaimId == fitment.Id && row.PartId != null, Ct));
        }
        finally
        {
            File.Delete(path);
        }
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
