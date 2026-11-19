using System.Data.Common;
using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Execution;

public sealed class CampaignStore(ResearchDb db)
{
    public async Task<IAsyncDisposable> LockAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var connection = db.Database.GetDbConnection();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(7352704015538084)";
            if (await command.ExecuteScalarAsync(ct) is not true)
                throw new IngestionException("Another research command is updating this database. Retry when it finishes.");
            return new MutationLock(connection, db);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async Task<ResearchCampaign> StartAsync(string name, CampaignMode mode, CancellationToken ct)
    {
        var existing = await db.Campaigns.SingleOrDefaultAsync(item => item.Name == name, ct);
        if (existing is not null)
        {
            if (existing.Mode != mode)
                throw new IngestionException("The campaign already exists in a different mode.");
            return existing;
        }

        var model = await db.VehicleModels.SingleOrDefaultAsync(item => item.Make == Ha4Plan.Make && item.Model == Ha4Plan.Model, ct);
        if (model is null)
        {
            model = new VehicleModel { Make = Ha4Plan.Make, Model = Ha4Plan.Model };
            db.VehicleModels.Add(model);
        }
        var variant = await db.VehicleVariants.SingleOrDefaultAsync(item => item.VehicleModelId == model.Id &&
            item.Chassis == Ha4Plan.Chassis && item.Engine == Ha4Plan.Engine, ct);
        if (variant is null)
        {
            variant = new VehicleVariant { VehicleModelId = model.Id, Chassis = Ha4Plan.Chassis, Engine = Ha4Plan.Engine, Era = Ha4Plan.Era };
            db.VehicleVariants.Add(variant);
        }
        var campaign = new ResearchCampaign { Name = name, Mode = mode, VehicleVariantId = variant.Id };
        db.Campaigns.Add(campaign);
        var topics = Ha4Plan.Topics.ToDictionary(topic => topic.Key, topic =>
            new ResearchTopic { CampaignId = campaign.Id, Key = topic.Key, Title = topic.Title });
        db.Topics.AddRange(topics.Values);
        db.Queries.AddRange(Ha4Plan.Queries().Select((query, priority) => new SearchQuery
        {
            CampaignId = campaign.Id, TopicId = topics[query.Topic].Id,
            Text = query.Text, Language = query.Language, Priority = priority
        }));
        await db.SaveChangesAsync(ct);
        return campaign;
    }

    public async Task<ResearchCampaign> FindAsync(string name, CancellationToken ct) =>
        await db.Campaigns.SingleOrDefaultAsync(item => item.Name == name, ct) ??
        throw new IngestionException("Campaign not found. Run start first.");

    public async Task ApplyPoliciesAsync(ResearchCampaign campaign, IReadOnlyList<PolicyInput> inputs, CancellationToken ct)
    {
        var policies = await db.Policies.Where(item => item.CampaignId == campaign.Id).ToListAsync(ct);
        foreach (var input in inputs)
        {
            var policy = policies.SingleOrDefault(item => item.Key == input.Key);
            if (policy is null)
            {
                policy = new SourcePolicy { CampaignId = campaign.Id };
                db.Policies.Add(policy);
                policies.Add(policy);
            }
            PolicyRules.Apply(input, policy);
        }

        foreach (var policy in policies.Where(policy => policy.AllowFetch))
        {
            if (policy.SitemapUrl is { } sitemap)
                await EnsureResourceAsync(campaign.Id, sitemap, policy.Publisher + " sitemap", ResourceKind.Sitemap, 100, ct);
            if (!policy.Prefix && policy.AllowAi && policy.AllowEvidence)
                await EnsureResourceAsync(campaign.Id, policy.Scope, policy.Publisher, ResourceKind.Technical, 85, ct);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureResourceAsync(Guid campaign, string url, string title, ResourceKind kind, int score, CancellationToken ct)
    {
        var local = db.Resources.Local.FirstOrDefault(item => item.CampaignId == campaign && item.Url == url);
        var resource = local ?? await db.Resources.SingleOrDefaultAsync(item => item.CampaignId == campaign && item.Url == url, ct);
        if (resource is null)
        {
            resource = new DiscoveredResource { CampaignId = campaign, Url = url, Host = new Uri(url).IdnHost, Title = BraveSearchProvider.Clean(title, 200) };
            db.Resources.Add(resource);
            db.Jobs.Add(new CrawlJob { Resource = resource });
        }
        else if (!db.Jobs.Local.Any(job => job.ResourceId == resource.Id) && !await db.Jobs.AnyAsync(job => job.ResourceId == resource.Id, ct))
            db.Jobs.Add(new CrawlJob { Resource = resource });
        if (kind == ResourceKind.Sitemap || resource.Kind != ResourceKind.Sitemap && resource.Relevance < score)
            resource.Kind = kind;
        resource.Relevance = Math.Max(resource.Relevance, score);
    }

    public async Task RecoverAsync(Guid campaign, int retries, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var interrupted = await db.Runs.Where(run => run.CampaignId == campaign && run.EndedAt == null).ToListAsync(ct);
        foreach (var run in interrupted)
            run.EndedAt = now;
        var ids = interrupted.Select(item => item.Id).ToArray();
        var searches = await db.SearchAttempts.Where(item => ids.Contains(item.RunId) && item.EndedAt == null).ToListAsync(ct);
        foreach (var attempt in searches)
        {
            attempt.EndedAt = now;
            attempt.Error = "Interrupted before a result was saved.";
        }
        var crawls = await db.CrawlAttempts.Where(item => ids.Contains(item.RunId) && item.EndedAt == null).ToListAsync(ct);
        foreach (var attempt in crawls)
        {
            attempt.EndedAt = now;
            attempt.Error = "Interrupted before a result was saved.";
        }
        foreach (var query in await db.Queries.Where(query => query.CampaignId == campaign && query.State == WorkState.Running).ToListAsync(ct))
        {
            query.State = query.Attempts < retries ? WorkState.Pending : WorkState.Failed;
            query.DueAt = now;
        }
        foreach (var job in await db.Jobs.Where(job => job.Resource.CampaignId == campaign && job.State == WorkState.Running).ToListAsync(ct))
        {
            job.State = job.Attempts < retries ? WorkState.Pending : WorkState.Failed;
            job.DueAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RetryFailedAsync(Guid campaign, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var search = await db.Queries.Where(item => item.CampaignId == campaign && item.State == WorkState.Failed)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, WorkState.Pending)
                .SetProperty(item => item.Attempts, 0).SetProperty(item => item.DueAt, now), ct);
        var crawl = await db.Jobs.Where(item => item.Resource.CampaignId == campaign && item.State == WorkState.Failed)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.State, WorkState.Pending)
                .SetProperty(item => item.Attempts, 0).SetProperty(item => item.DueAt, now), ct);
        return search + crawl;
    }

    public async Task ReviewAsync(ResearchCampaign campaign, Guid id, Decision decision, string note, CancellationToken ct)
    {
        var claim = await db.Claims.SingleOrDefaultAsync(item => item.CampaignId == campaign.Id && item.Id == id, ct);
        if (claim is null || claim.State != Decision.Pending)
            throw new IngestionException("Choose a pending claim in this campaign.");

        claim.State = decision;
        var review = new ReviewDecision { ClaimId = id, State = decision, Note = note };
        db.Decisions.Add(review);
        if (decision == Decision.Approved && campaign.Mode == CampaignMode.Live)
        {
            Guid? partId = null;
            if (claim.Manufacturer is { } manufacturer && claim.PartNumber is { } number && claim.PartNumberKey is { } partKey)
            {
                var makerKey = ClaimIdentity.ManufacturerKey(manufacturer);
                var part = await db.Parts.SingleOrDefaultAsync(item => item.ManufacturerKey == makerKey && item.PartNumberKey == partKey, ct);
                if (part is null)
                {
                    part = new CatalogPart { Manufacturer = manufacturer, ManufacturerKey = makerKey, PartNumberKey = partKey, Name = claim.Subject };
                    db.Parts.Add(part);
                }
                if (!await db.PartIdentifiers.AnyAsync(item => item.PartId == part.Id && item.Number == number && item.Kind == claim.Kind, ct))
                    db.PartIdentifiers.Add(new PartIdentifier { PartId = part.Id, Number = number, Kind = claim.Kind });
                partId = part.Id;
            }
            db.Approved.Add(new ApprovedKnowledge
            {
                ClaimId = claim.Id, VehicleVariantId = campaign.VehicleVariantId, PartId = partId, DecisionId = review.Id
            });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> ExportAsync(ResearchCampaign campaign, string path, int offset, CancellationToken ct)
    {
        if (campaign.Mode != CampaignMode.Live)
            throw new IngestionException("Synthetic research is isolated from catalog export.");
        var payloads = await (from evidence in db.Evidence
                              join claim in db.Claims on evidence.ClaimId equals claim.Id
                              join approval in db.Approved on claim.Id equals approval.ClaimId
                              where claim.CampaignId == campaign.Id && claim.State == Decision.Approved && evidence.FitmentJson != null
                              orderby approval.ApprovedAt, claim.Id, evidence.Id
                              select evidence.FitmentJson!).ToListAsync(ct);
        var import = new CatalogImport([Ha4Plan.Chassis]);
        var entries = payloads.Select(row => JsonSerializer.Deserialize<FitmentEntry>(row, CatalogImport.JsonOptions))
            .Select((row, index) => import.Validate(row, index + 1)).DistinctBy(CatalogImport.ClaimKey)
            .Skip(offset).Take(CatalogImport.MaxEntries).ToArray();
        var count = entries.Length;
        string payload;
        while (true)
        {
            try
            {
                payload = import.Serialize(entries.AsSpan(0, count).ToArray());
                break;
            }
            catch (CatalogImportException) when (count > 1)
            {
                // Individual rows were validated above. Shrink only the batch to fit the inbox character limit.
                count--;
            }
        }
        await WriteNewAsync(path, payload, ct);
        return count;
    }

    internal static async Task WriteNewAsync(string path, string content, CancellationToken ct)
    {
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, ct);
            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class MutationLock(DbConnection connection, ResearchDb context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(7352704015538084)";
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
