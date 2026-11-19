using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Execution;

public sealed class CampaignReport(ResearchDb db)
{
    public async Task<object> StatusAsync(ResearchCampaign campaign, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().Where(row => row.CampaignId == campaign.Id).ToListAsync(ct);
        var resources = await db.Resources.AsNoTracking().Where(row => row.CampaignId == campaign.Id).ToListAsync(ct);
        var search = db.Queries.Where(row => row.CampaignId == campaign.Id);
        var jobs = db.Jobs.Where(row => row.Resource.CampaignId == campaign.Id);
        var claims = db.Claims.Where(row => row.CampaignId == campaign.Id);
        var versions = from version in db.Versions
                       join resource in db.Resources on version.ResourceId equals resource.Id
                       where resource.CampaignId == campaign.Id && resource.Kind != ResourceKind.Sitemap
                       select version;
        var runs = await db.Runs.AsNoTracking().Where(row => row.CampaignId == campaign.Id).ToListAsync(ct);
        var crawlErrors = from attempt in db.CrawlAttempts join run in db.Runs on attempt.RunId equals run.Id
                          where run.CampaignId == campaign.Id && attempt.Error != null
                          select new { Stage = "Work", attempt.StartedAt, attempt.Error, attempt.HttpStatus };
        var searchErrors = from attempt in db.SearchAttempts join run in db.Runs on attempt.RunId equals run.Id
                           where run.CampaignId == campaign.Id && attempt.Error != null
                           select new { Stage = "Search", attempt.StartedAt, attempt.Error, HttpStatus = (int?)null };

        return new
        {
            campaign.Name, campaign.Mode, Chassis = Ha4Plan.Chassis,
            TopicsCovered = await search.Where(row => row.State == WorkState.Done).Select(row => row.TopicId).Distinct().CountAsync(ct),
            SearchStates = await search.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            CrawlStates = await jobs.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            ClaimStates = await claims.GroupBy(row => row.State).Select(group => new { State = group.Key, Count = group.Count() }).ToListAsync(ct),
            UrlsDiscovered = resources.Count, RelevantResources = resources.Count(item => item.Relevance > 0),
            ApprovedSources = policy.Count(item => item.AllowFetch && item.AllowAi && item.AllowEvidence),
            ReadyResources = resources.Count(item => PolicyRules.Permitted(policy, item) is not null),
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
                Searches = runs.Sum(row => row.Searches), PageAttempts = runs.Sum(row => row.Pages),
                AiCalls = runs.Sum(row => row.AiCalls), InputTokens = runs.Sum(row => row.InputTokens),
                OutputTokens = runs.Sum(row => row.OutputTokens), EstimatedUsd = runs.Sum(row => row.EstimatedUsd),
                DuplicateUrls = runs.Sum(row => row.DuplicateUrls), DuplicateDocuments = runs.Sum(row => row.DuplicateDocuments),
                DuplicateClaims = runs.Sum(row => row.DuplicateClaims), WithheldClaims = runs.Sum(row => row.WithheldClaims),
                InterruptedOrRunning = runs.Count(row => row.EndedAt == null)
            }
        };
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
