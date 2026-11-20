using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Execution;

public sealed class CampaignReport(ResearchDb db)
{
    public async Task<object> StatusAsync(ResearchCampaign campaign, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().Where(row => row.CampaignId == campaign.Id).ToListAsync(ct);
        var resources = db.Resources.AsNoTracking().Where(row => row.CampaignId == campaign.Id);
        var ready = 0;
        await foreach (var item in resources.Select(r => new DiscoveredResource { Url = r.Url, Kind = r.Kind })
                           .AsAsyncEnumerable().WithCancellation(ct))
            if (PolicyRules.Permitted(policy, item) != null) ready++;
        var search = db.Queries.Where(row => row.CampaignId == campaign.Id);
        var jobs = db.Jobs.Where(row => row.Resource.CampaignId == campaign.Id);
        var claims = db.Claims.Where(row => row.CampaignId == campaign.Id);
        var versions = from version in db.Versions
                       join resource in db.Resources on version.ResourceId equals resource.Id
                       where resource.CampaignId == campaign.Id && resource.Kind != ResourceKind.Sitemap
                       select version;
        var runs = db.Runs.AsNoTracking().Where(row => row.CampaignId == campaign.Id);
        var byProvider = await runs.GroupBy(r => r.Provider ?? "unrecorded").Select(g => new
        {
            Provider = g.Key, Calls = g.Sum(r => (long)r.AiCalls), InputTokens = g.Sum(r => r.InputTokens),
            OutputTokens = g.Sum(r => r.OutputTokens), Searches = g.Sum(r => (long)r.Searches), Pages = g.Sum(r => (long)r.Pages),
            EstimatedUsd = g.Sum(r => r.EstimatedUsd), DuplicateUrls = g.Sum(r => (long)r.DuplicateUrls),
            DuplicateDocuments = g.Sum(r => (long)r.DuplicateDocuments), DuplicateClaims = g.Sum(r => (long)r.DuplicateClaims),
            WithheldClaims = g.Sum(r => (long)r.WithheldClaims), Active = g.Count(r => r.EndedAt == null)
        }).ToListAsync(ct);
        var crawlErrors = from attempt in db.CrawlAttempts join run in db.Runs on attempt.RunId equals run.Id
                          where run.CampaignId == campaign.Id && attempt.Error != null
                          select new { Stage = "Work", attempt.StartedAt, attempt.Error, attempt.HttpStatus };
        var searchErrors = from attempt in db.SearchAttempts join run in db.Runs on attempt.RunId equals run.Id
                           where run.CampaignId == campaign.Id && attempt.Error != null
                           select new { Stage = "Search", attempt.StartedAt, attempt.Error, HttpStatus = (int?)null };

        return new
        {
            campaign.Name, campaign.Mode,
            Chassis = await db.VehicleVariants.Where(v => v.Id == campaign.VehicleVariantId).Select(v => v.Chassis).SingleAsync(ct),
            Control = await db.Campaigns.Where(c => c.Id == campaign.Id).Select(c => new { c.StopRequested, c.SearchAfter, c.ExtractionAfter }).SingleAsync(ct),
            LatestRun = await runs.OrderByDescending(r => r.StartedAt).Select(r => new { r.Id, r.Command, r.Provider, r.StartedAt, r.EndedAt, r.StopReason }).FirstOrDefaultAsync(ct),
            ProviderUsage = byProvider.Select(r => new { r.Provider, r.Calls, r.InputTokens, r.OutputTokens }),
            TopicsCovered = await search.Where(row => row.State == WorkState.Done).Select(row => row.TopicId).Distinct().CountAsync(ct),
            SearchStates = await search.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            CrawlStates = await jobs.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            ClaimStates = await claims.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            UrlsDiscovered = await resources.CountAsync(ct), RelevantResources = await resources.CountAsync(item => item.Relevance > 0, ct),
            ApprovedSources = policy.Count(item => item.AllowFetch && item.AllowAi && item.AllowEvidence),
            ReadyResources = ready,
            DocumentsFetched = await versions.CountAsync(ct),
            DocumentsProcessed = await versions.CountAsync(row => row.ProcessedAt != null, ct),
            Claims = await claims.CountAsync(ct),
            ConflictClaims = await claims.CountAsync(row => row.Conflict, ct),
            PossibleMatches = await claims.CountAsync(row => row.PossibleMatch, ct),
            RecentErrors = await crawlErrors.Concat(searchErrors).OrderByDescending(row => row.StartedAt).Take(8).ToListAsync(ct),
            NextSearchDueAt = await search.Where(row => row.State == WorkState.Pending).MinAsync(row => (DateTimeOffset?)row.DueAt, ct),
            NextCrawlDueAt = await jobs.Where(row => row.State == WorkState.Pending).MinAsync(row => (DateTimeOffset?)row.DueAt, ct),
            Usage = new
            {
                Searches = byProvider.Sum(row => row.Searches), PageAttempts = byProvider.Sum(row => row.Pages),
                AiCalls = byProvider.Sum(row => row.Calls), InputTokens = byProvider.Sum(row => row.InputTokens),
                OutputTokens = byProvider.Sum(row => row.OutputTokens), EstimatedUsd = byProvider.Sum(row => row.EstimatedUsd),
                DuplicateUrls = byProvider.Sum(row => row.DuplicateUrls), DuplicateDocuments = byProvider.Sum(row => row.DuplicateDocuments),
                DuplicateClaims = byProvider.Sum(row => row.DuplicateClaims), WithheldClaims = byProvider.Sum(row => row.WithheldClaims),
                InterruptedOrRunning = byProvider.Sum(row => row.Active)
            }
        };
    }

    public async Task<object> JobsAsync(Guid campaign, int offset, CancellationToken ct)
    {
        var search = db.Queries.Where(q => q.CampaignId == campaign && q.State != WorkState.Done)
            .Select(q => new { q.Id, Stage = "Search", Item = q.Text, q.State, q.Attempts, q.DueAt });
        var crawl = db.Jobs.Where(j => j.Resource.CampaignId == campaign && j.State != WorkState.Done)
            .Select(j => new { j.Id, Stage = "Crawl", Item = j.Resource.Url, j.State, j.Attempts, j.DueAt });
        return await search.Concat(crawl).OrderBy(j => j.State).ThenBy(j => j.DueAt).ThenBy(j => j.Id)
            .Skip(offset).Take(100).ToListAsync(ct);
    }

    public async Task<object> CoverageAsync(Guid campaign, CancellationToken ct)
    {
        var claims = db.Claims.Where(c => c.CampaignId == campaign && c.State != Decision.Rejected);
        var counts = await claims.GroupBy(c => c.Kind).Select(g => new { Kind = g.Key, Count = g.Count() }).ToListAsync(ct);
        var topics = await db.Topics.Where(t => t.CampaignId == campaign).OrderBy(t => t.Key).Select(t => new
        {
            t.Key, t.Title, SearchesCompleted = db.Queries.Count(q => q.TopicId == t.Id && q.State == WorkState.Done)
        }).ToListAsync(ct);
        var japaneseClaims = await (from e in db.Evidence join c in claims on e.ClaimId equals c.Id
                                    where e.Version.Language != null && e.Version.Language.StartsWith("ja")
                                    select e.ClaimId).Distinct().CountAsync(ct);
        var gaps = await (from q in db.Queries join c in claims on q.OriginClaimId equals c.Id
                          where q.CampaignId == campaign
                          orderby q.Priority, q.Text
                          select new { q.Id, q.Text, q.OriginClaimId, q.GapReason, SearchState = q.State,
                              Resolved = q.GapPartNumber != null ? claims.Any(id => id.PartNumber == q.GapPartNumber &&
                                  (id.Kind == FactKind.PartIdentity || id.Kind == FactKind.OemPartNumber)) :
                                  q.GapReason == GapDiscovery.ConflictReason ? !claims.Any(other => other.GroupKey == c.GroupKey && other.State == Decision.Pending) :
                                  db.Evidence.Where(e => e.ClaimId == c.Id).Select(e => e.Version.ContentId).Distinct().Count() >= 2
                          }).ToListAsync(ct);
        return new { Topics = topics.Select(t => new { t.Key, t.Title, t.SearchesCompleted,
            CandidateOrApprovedClaims = t.Key == "japanese" ? japaneseClaims : counts.Where(c => GapDiscovery.Topic(c.Kind) == t.Key).Sum(c => c.Count) }),
            UnresolvedGaps = gaps.Where(g => !g.Resolved), ResolvedGaps = gaps.Count(g => g.Resolved) };
    }

    public async Task<object> SourcesAsync(Guid campaign, int offset, CancellationToken ct)
    {
        var policies = await db.Policies.AsNoTracking().Where(row => row.CampaignId == campaign).ToListAsync(ct);
        var items = await db.Resources.AsNoTracking().Where(row => row.CampaignId == campaign)
            .OrderByDescending(row => row.Relevance).ThenBy(row => row.Url).Skip(offset).Take(100).ToListAsync(ct);
        var ids = items.Select(row => row.Id).ToArray();
        var jobs = await db.Jobs.AsNoTracking().Where(row => ids.Contains(row.ResourceId)).ToDictionaryAsync(row => row.ResourceId, ct);
        return items.Select(row => new
        {
            row.Id, row.Url, row.Title, row.Kind, row.Relevance, row.LastRetrievedAt,
            Approval = PolicyRules.Permitted(policies, row)?.Key, Queue = jobs.GetValueOrDefault(row.Id)?.State
        }).ToArray();
    }

    public async Task<object> ClaimsAsync(Guid campaign, int offset, CancellationToken ct)
    {
        var claims = await db.Claims.AsNoTracking().Where(row => row.CampaignId == campaign)
            .OrderBy(row => row.Kind).ThenBy(row => row.Id).Skip(offset).Take(100).ToListAsync(ct);
        var ids = claims.Select(row => row.Id).ToArray();
        var evidence = await (from excerpt in db.Evidence.AsNoTracking()
                              join version in db.Versions on excerpt.VersionId equals version.Id
                              join content in db.Contents on version.ContentId equals content.Id
                              join resource in db.Resources on version.ResourceId equals resource.Id
                              where ids.Contains(excerpt.ClaimId)
                              select new
                              {
                                  excerpt.ClaimId, DocumentId = resource.Id, VersionId = version.Id, resource.Url,
                                  version.Publisher, version.SourceKind, version.Language, version.RetrievedAt,
                                  content.Sha256, excerpt.Locator, excerpt.Passage, excerpt.EnglishTranslation
                              }).ToListAsync(ct);
        var byClaim = evidence.ToLookup(row => row.ClaimId);
        return claims.Select(row => new { Claim = row, Evidence = byClaim[row.Id].ToArray() }).ToArray();
    }
}
