using System.Security.Cryptography;
using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Research.Collection;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Execution;

public sealed class CampaignRunner(ResearchDb db, CampaignStore store, ISearchProvider? search,
    IResearchCollector? collector, IKnowledgeExtractor? extractor)
{
    private VehicleVariant variant = null!;
    public async Task<ResearchRun> RunAsync(ResearchCampaign campaign, string command, RunLimits limits,
        bool discover, bool work, CancellationToken ct)
    {
        limits.Validate();
        variant = await db.VehicleVariants.SingleAsync(v => v.Id == campaign.VehicleVariantId, ct);
        await store.RecoverAsync(campaign.Id, limits.Retries, ct);
        var run = new ResearchRun { CampaignId = campaign.Id, Command = command, Provider = extractor?.Name };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        var budget = new RunBudget(limits, run, campaign.Mode == CampaignMode.Offline, extractor is CodexKnowledgeExtractor);
        var policies = await db.Policies.Where(item => item.CampaignId == campaign.Id).ToListAsync(ct);
        if (discover && search is not null && !(campaign.SearchAfter > DateTimeOffset.UtcNow))
            await DiscoverAsync(campaign, limits, run, budget, policies, ct);
        if (work && collector is not null && run.StopReason is null && !(campaign.ExtractionAfter > DateTimeOffset.UtcNow))
            await WorkAsync(campaign, limits, run, budget, policies, ct);
        run.EndedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return run;
    }

    private async Task DiscoverAsync(ResearchCampaign campaign, RunLimits limits, ResearchRun run,
        RunBudget budget, IReadOnlyList<SourcePolicy> policies, CancellationToken ct)
    {
        while (budget.CanSearch && !await StoppingAsync(campaign.Id, ct))
        {
            var now = DateTimeOffset.UtcNow;
            var query = await db.Queries.Where(item => item.CampaignId == campaign.Id &&
                    item.State == WorkState.Pending && item.DueAt <= now && item.Attempts < limits.Retries)
                .OrderBy(item => item.Priority).FirstOrDefaultAsync(ct);
            if (query is null)
                break;
            if (campaign.Mode == CampaignMode.Live && campaign.SearchAfter is { } after && after > now)
                await Task.Delay(after - now, ct);
            budget.ReserveSearch();
            query.State = WorkState.Running;
            query.Attempts++;
            var attempt = new SearchAttempt { RunId = run.Id, QueryId = query.Id };
            db.SearchAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            try
            {
                var hits = await search!.SearchAsync(query, ct);
                foreach (var hit in hits.Take(20))
                    await AddHitAsync(campaign.Id, hit, query, null, policies, linksOnly: false, run, ct);
                query.State = WorkState.Done;
                attempt.Returned = hits.Count;
                if (campaign.Mode == CampaignMode.Live) campaign.SearchAfter = DateTimeOffset.UtcNow.AddSeconds(1);
            }
            catch (IngestionException error)
            {
                var providerHold = error.HttpStatus is 401 or 403 or 429;
                query.State = providerHold || error.IsTransient && query.Attempts < limits.Retries
                    ? WorkState.Pending : WorkState.Failed;
                if (providerHold)
                    query.Attempts--;
                query.DueAt = providerHold ? DateTimeOffset.UtcNow : RetryPolicy.Due(query.Attempts, error.RetryAt);
                if (error.IsTransient || providerHold)
                    campaign.SearchAfter = RetryPolicy.Due(query.Attempts, error.RetryAt);
                if (providerHold)
                    run.StopReason = error.Message;
                attempt.Error = error.Message;
                attempt.EndedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                // Provider failures are shared by all pending queries. Resume after the backoff or credential fix.
                break;
            }
            attempt.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task AddHitAsync(Guid campaign, SearchHit hit, SearchQuery? query, DiscoveredResource? parent,
        IReadOnlyList<SourcePolicy> policies, bool linksOnly, ResearchRun run, CancellationToken ct)
    {
        var uri = UrlRules.Normalize(hit.Url);
        if (uri is null)
            return;
        var url = uri.AbsoluteUri;
        if (linksOnly && PolicyRules.Match(policies, url) is not { AllowFetch: true, AllowAi: true, AllowEvidence: true })
            return;
        var title = BraveSearchProvider.Clean(hit.Title, 200);
        var snippet = BraveSearchProvider.Clean(hit.Snippet, 500);
        var (kind, relevance) = Relevance.Classify(uri, title, snippet, variant.Chassis, variant.Engine);
        var existing = db.Resources.Local.FirstOrDefault(item => item.CampaignId == campaign && item.Url == url) ??
            await db.Resources.SingleOrDefaultAsync(item => item.CampaignId == campaign && item.Url == url, ct);
        if (existing is not null)
        {
            run.DuplicateUrls++;
            // Discovery from a better query may surface application details the first hit lacked.
            if (existing.Kind != ResourceKind.Sitemap && relevance > existing.Relevance)
            {
                if (existing.Relevance == 0)
                    db.Jobs.Add(new CrawlJob { Resource = existing,
                        State = PolicyRules.Permitted(policies, existing) is null ? WorkState.Blocked : WorkState.Pending });
                existing.Kind = kind;
                existing.Relevance = relevance;
                existing.Title = title;
                existing.Snippet = snippet;
                existing.Language = query?.Language ?? parent?.Language;
            }
            return;
        }
        var resource = new DiscoveredResource
        {
            CampaignId = campaign, SearchQueryId = query?.Id, ParentId = parent?.Id, Url = url,
            Host = uri.IdnHost, Title = title, Snippet = snippet, Language = query?.Language ?? parent?.Language,
            Kind = kind, Relevance = relevance
        };
        db.Resources.Add(resource);
        if (kind != ResourceKind.Irrelevant)
            db.Jobs.Add(new CrawlJob { Resource = resource,
                State = PolicyRules.Permitted(policies, resource) is null ? WorkState.Blocked : WorkState.Pending });
    }

    private async Task WorkAsync(ResearchCampaign campaign, RunLimits limits, ResearchRun run, RunBudget budget,
        IReadOnlyList<SourcePolicy> policies, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (limits.RefreshHours is { } hours)
        {
            var cutoff = now.AddHours(-hours);
            await db.Jobs.Where(j => j.Resource.CampaignId == campaign.Id && j.State == WorkState.Done &&
                j.Resource.LastRetrievedAt <= cutoff).ExecuteUpdateAsync(u => u.SetProperty(j => j.State, WorkState.Pending)
                    .SetProperty(j => j.Attempts, 0).SetProperty(j => j.DueAt, now), ct);
        }
        // A database-wide command lease and one active request per origin bound parallelism.
        while (budget.CanFetch && run.StopReason is null && !await StoppingAsync(campaign.Id, ct))
        {
            now = DateTimeOffset.UtcNow;
            var canAnalyze = extractor is not null && budget.CanAnalyze;
            var pending = await db.Jobs.Include(job => job.Resource)
                .Where(job => job.Resource.CampaignId == campaign.Id && job.State == WorkState.Pending &&
                              job.DueAt <= now && job.Attempts < limits.Retries && (canAnalyze || job.Resource.Kind == ResourceKind.Sitemap))
                .OrderByDescending(job => job.Resource.Relevance).ThenBy(job => job.Resource.Url)
                .Take(128).ToListAsync(ct);
            if (pending.Count == 0) break;
            var isOffline = campaign.Mode == CampaignMode.Offline;
            var originCutoff = now.AddSeconds(-300);
            var liveAttempts = from attempt in db.CrawlAttempts
                               join previousRun in db.Runs on attempt.RunId equals previousRun.Id
                               join previousCampaign in db.Campaigns on previousRun.CampaignId equals previousCampaign.Id
                               join previousJob in db.Jobs on attempt.JobId equals previousJob.Id
                               where previousCampaign.Mode == CampaignMode.Live && (attempt.EndedAt ?? attempt.StartedAt) > originCutoff
                               select new { previousJob.Resource.Host, At = attempt.EndedAt ?? attempt.StartedAt };
            var recent = isOffline ? [] : await liveAttempts
                .GroupBy(item => item.Host)
                .Select(group => new OriginTiming(group.Key, group.Max(entry => entry.At))).ToListAsync(ct);
            var batch = new List<FetchWork>();
            var hosts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var job in pending)
            {
                var policy = PolicyRules.Permitted(policies, job.Resource);
                if (policy is null)
                {
                    job.State = WorkState.Blocked;
                    continue;
                }
                if (!isOffline)
                {
                    var last = recent.FirstOrDefault(value => value.Host == job.Resource.Host);
                    if (last is not null && last.At.AddSeconds(policy.DelaySeconds) is var next && next > now)
                    {
                        job.DueAt = next;
                        continue;
                    }
                    if (!hosts.Add(job.Resource.Host))
                        continue;
                }
                if (!budget.ReservePage())
                    break;
                job.State = WorkState.Running;
                job.Attempts++;
                var attempt = new CrawlAttempt { RunId = run.Id, JobId = job.Id, PolicyId = policy.Id };
                db.CrawlAttempts.Add(attempt);
                batch.Add(new(job, policy, attempt));
                if (batch.Count >= limits.Concurrency)
                    break;
            }
            await db.SaveChangesAsync(ct);
            if (batch.Count == 0)
                continue; // All selected jobs were blocked or given a later due time.

            // Only retained, unprocessed versions under the same current permission can satisfy a retry locally.
            var resumable = batch.Where(w => w.Policy.AllowSnapshot && w.Job.Resource.Kind != ResourceKind.Sitemap)
                .Select(w => w.Job.ResourceId).ToArray();
            var snapshots = await (from v in db.Versions.AsNoTracking() join c in db.Contents on v.ContentId equals c.Id
                                   join r in db.Resources on v.ResourceId equals r.Id
                                   where resumable.Contains(v.ResourceId) && v.LastSeenAt == r.LastRetrievedAt && v.ProcessedAt == null &&
                                       v.Snapshot != null && v.SizeBytes <= limits.MaximumBytes
                                   orderby v.LastSeenAt descending
                                   select new { v.ResourceId, v.PolicyId, v.Snapshot, v.RetrievedAt, c.EncodingKey }).ToListAsync(ct);
            var fetched = await Task.WhenAll(batch.Select(item =>
            {
                var saved = snapshots.FirstOrDefault(v => v.ResourceId == item.Job.ResourceId && v.PolicyId == item.Policy.Id);
                return saved == null ? FetchAsync(item, limits.MaximumBytes, ct) :
                    Task.FromResult(new FetchResult(new CollectedDocument(saved.Snapshot!, saved.RetrievedAt, saved.EncodingKey), null, true));
            }));
            for (var i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                var outcome = fetched[i];
                if (run.StopReason is not null)
                {
                    item.Job.State = WorkState.Pending;
                    item.Job.Attempts--;
                    item.Attempt.Error = "Deferred after provider pause.";
                }
                else if (outcome.Error is { } fetchError)
                    Fail(item, fetchError, limits, fetched: false);
                else
                {
                    if (!isOffline && !outcome.FromSnapshot)
                        item.Attempt.HttpStatus = 200;
                    try
                    {
                        await ProcessAsync(campaign.Id, item, outcome.Document!, limits, run, budget, policies, ct);
                    }
                    catch (IngestionException error)
                    {
                        if (item.Attempt.AiReserved && error.HttpStatus is 401 or 403 or 429)
                        {
                            run.StopReason = error.Message;
                            campaign.ExtractionAfter = RetryPolicy.Due(item.Job.Attempts, error.RetryAt);
                        }
                        Fail(item, error, limits, fetched: true);
                    }
                }
                item.Attempt.EndedAt ??= DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task<FetchResult> FetchAsync(FetchWork work, int maximumBytes, CancellationToken ct)
    {
        try
        {
            return new(await collector!.FetchAsync(work.Job.Resource, work.Policy, maximumBytes, ct), null);
        }
        catch (IngestionException error)
        {
            return new(null, error);
        }
        catch (IOException error)
        {
            return new(null, new IngestionException("A document or collector state file could not be read.", error) { IsTransient = true });
        }
    }

    private async Task ProcessAsync(Guid campaign, FetchWork work, CollectedDocument document, RunLimits limits,
        ResearchRun run, RunBudget budget, IReadOnlyList<SourcePolicy> policies, CancellationToken ct)
    {
        var job = work.Job;
        var resource = job.Resource;
        var policy = work.Policy;
        var source = ResearchHttpCollector.Source(resource, policy);
        var sitemap = resource.Kind == ResourceKind.Sitemap;
        var hash = Convert.ToHexStringLower(SHA256.HashData(document.Bytes));
        var encoding = (sitemap ? "sitemap:" : "") + (document.Charset?.Trim('"').ToLowerInvariant() ?? "");
        var content = await db.Contents.SingleOrDefaultAsync(item => item.CampaignId == campaign &&
            item.Sha256 == hash && item.Format == policy.Format && item.EncodingKey == encoding, ct);
        if (content is null)
        {
            content = new DocumentContent { CampaignId = campaign, Sha256 = hash, Format = policy.Format, EncodingKey = encoding };
            db.Contents.Add(content);
        }
        else
            run.DuplicateDocuments++;

        var version = await db.Versions.SingleOrDefaultAsync(item => item.ResourceId == resource.Id && item.ContentId == content.Id, ct);
        if (version is null)
        {
            version = new DocumentVersion
            {
                ResourceId = resource.Id, ContentId = content.Id, PolicyId = policy.Id, Publisher = policy.Publisher,
                SourceKind = policy.Kind, Language = source.Language, RetrievedAt = document.CollectedAtUtc,
                SizeBytes = document.Bytes.Length, Snapshot = policy.AllowSnapshot ? document.Bytes : null
            };
            db.Versions.Add(version);
        }
        version.LastSeenAt = document.CollectedAtUtc;
        resource.LastRetrievedAt = document.CollectedAtUtc;
        work.Attempt.VersionId = version.Id;
        await db.SaveChangesAsync(ct);

        if (version.ProcessedAt is not null)
        {
            job.State = WorkState.Done;
            return;
        }
        if (sitemap)
        {
            foreach (var link in LinkDiscovery.FromSitemap(document, new Uri(resource.Url)))
                await AddHitAsync(campaign, link, null, resource, policies, linksOnly: true, run, ct);
        }
        else
        {
            var normalized = DocumentNormalizer.Normalize(document, policy.Format);
            source = source with { Language = source.Language ?? normalized.Language };
            version.Language = source.Language;
            AnalysisResult result;
            if (content.AnalysisJson is { } json)
                result = JsonSerializer.Deserialize<AnalysisResult>(json, CatalogImport.JsonOptions)!;
            else
            {
                if (!budget.ReserveAnalysis())
                {
                    job.Attempts--;
                    job.State = WorkState.Pending;
                    job.DueAt = DateTimeOffset.UtcNow;
                    return;
                }
                work.Attempt.AiReserved = true;
                await db.SaveChangesAsync(ct);
                var reply = await extractor!.ExtractAsync(new(normalized, [variant.Chassis], source.Language)
                { Provenance = new(source.Url, source.Publisher, version.Id, hash, version.RetrievedAt) }, ct);
                run.InputTokens += reply.InputTokens;
                run.OutputTokens += reply.OutputTokens;
                result = await KnowledgeAnalysis.ValidateAsync(reply, source, document, normalized, ct, variant.Chassis);
                content.AnalysisJson = JsonSerializer.Serialize(result, CatalogImport.JsonOptions);
                content.AnalyzedAt = DateTimeOffset.UtcNow;
                run.WithheldClaims += result.Withheld;
                run.DuplicateClaims += result.Duplicates;
            }

            foreach (var fact in result.Facts)
                await SaveClaimAsync(campaign, fact, version, source, run, ct);
            if (policy.ExploreLinks && policy.Format == KeiBuild.Ingestion.Sources.DocumentFormat.Html)
            {
                foreach (var link in LinkDiscovery.FromHtml(document, new Uri(resource.Url)))
                    await AddHitAsync(campaign, link, null, resource, policies, linksOnly: true, run, ct);
            }
        }
        version.ProcessedAt = DateTimeOffset.UtcNow;
        job.State = WorkState.Done;
        job.Attempts = 0;
    }

    private async Task SaveClaimAsync(Guid campaign, GroundedFact fact, DocumentVersion version,
        KeiBuild.Ingestion.Sources.SourceDefinition source, ResearchRun run, CancellationToken ct)
    {
        var group = ClaimIdentity.Group(fact, variant.Chassis);
        var key = ClaimIdentity.Claim(fact, group);
        var claim = db.Claims.Local.FirstOrDefault(item => item.CampaignId == campaign && item.ClaimKey == key) ??
                    await db.Claims.SingleOrDefaultAsync(item => item.CampaignId == campaign && item.ClaimKey == key, ct);
        if (claim is null)
        {
            claim = new KnowledgeClaim
            {
                CampaignId = campaign, ClaimKey = key, GroupKey = group, Kind = fact.Kind, Subject = fact.Subject,
                Attribute = fact.Attribute, Value = fact.Value, Unit = fact.Unit, Manufacturer = fact.Manufacturer,
                PartNumber = fact.PartNumber, PartNumberKey = fact.PartNumber is null ? null : ClaimIdentity.PartNumberKey(fact.PartNumber),
                RelatedPartNumber = fact.RelatedPartNumber, Applicability = fact.Applicability
            };
            var related = await db.Claims.Where(item => item.CampaignId == campaign && (item.GroupKey == group ||
                claim.PartNumberKey != null && item.PartNumberKey == claim.PartNumberKey)).ToListAsync(ct);
            related.AddRange(db.Claims.Local.Where(item => item.CampaignId == campaign && (item.GroupKey == group ||
                claim.PartNumberKey != null && item.PartNumberKey == claim.PartNumberKey)));
            foreach (var other in related.DistinctBy(item => item.Id))
            {
                if (other.GroupKey == group)
                    other.Conflict = claim.Conflict = true;
                if (claim.PartNumberKey is not null && claim.PartNumberKey == other.PartNumberKey &&
                    claim.Manufacturer is not null && other.Manufacturer is not null &&
                    ClaimIdentity.ManufacturerKey(claim.Manufacturer) != ClaimIdentity.ManufacturerKey(other.Manufacturer))
                    other.PossibleMatch = claim.PossibleMatch = true;
            }
            db.Claims.Add(claim);
        }
        else
            run.DuplicateClaims++;
        if (db.Evidence.Local.Any(item => item.ClaimId == claim.Id && item.VersionId == version.Id && item.Locator == fact.Locator) ||
            await db.Evidence.AnyAsync(item => item.ClaimId == claim.Id && item.VersionId == version.Id && item.Locator == fact.Locator, ct))
            return;
        string? fitment = null;
        if (fact.Fitment is { } original)
        {
            var reference = new SourceReference(source.Title, source.Url, source.Kind, fact.Locator, fact.Passage);
            fitment = JsonSerializer.Serialize(original with { Source = reference }, CatalogImport.JsonOptions);
        }
        db.Evidence.Add(new ClaimEvidence
        {
            ClaimId = claim.Id, VersionId = version.Id, Locator = fact.Locator, Passage = fact.Passage,
            EnglishTranslation = fact.EnglishTranslation, FitmentJson = fitment
        });
    }

    private static void Fail(FetchWork work, IngestionException error, RunLimits limits, bool fetched)
    {
        var job = work.Job;
        var auth = fetched && error.HttpStatus is 401 or 403 or 429;
        var deferred = !fetched && error is { HttpStatus: null, RetryAt: not null };
        if (auth || deferred)
            job.Attempts--;
        job.State = auth || deferred || error.IsTransient && job.Attempts < limits.Retries
            ? WorkState.Pending : WorkState.Failed;
        job.DueAt = RetryPolicy.Due(job.Attempts, error.RetryAt);
        if (!fetched)
            work.Attempt.HttpStatus = error.HttpStatus;
        work.Attempt.Error = error.Message;
    }

    private Task<bool> StoppingAsync(Guid campaign, CancellationToken ct) =>
        db.Campaigns.Where(c => c.Id == campaign).Select(c => c.StopRequested).SingleAsync(ct);

    private sealed record FetchWork(CrawlJob Job, SourcePolicy Policy, CrawlAttempt Attempt);
    private sealed record FetchResult(CollectedDocument? Document, IngestionException? Error, bool FromSnapshot = false);
    private sealed record OriginTiming(string Host, DateTimeOffset At);
}
