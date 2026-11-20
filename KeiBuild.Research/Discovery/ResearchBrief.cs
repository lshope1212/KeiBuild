using KeiBuild.Research.Data;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Discovery;

public sealed record PlanCategory(string Key, int GroundedClaims, int CompletedSearches);
public sealed record ResearchBrief(IReadOnlyList<PlanCategory> Categories, IReadOnlyList<string> PreviousQueries,
    IReadOnlyList<string> EvidenceLeads, IReadOnlyList<string> DocumentReferences, int ProposalAllowance)
{
    public string Target { get; init; } = "";

    internal static ResearchBrief Empty { get; } = new([], [], [], [], 0);

    internal static async Task<ResearchBrief> LoadAsync(ResearchDb db, Guid campaign, int limit, CancellationToken ct)
    {
        var target = await (from c in db.Campaigns join v in db.VehicleVariants on c.VehicleVariantId equals v.Id
                            join m in db.VehicleModels on v.VehicleModelId equals m.Id where c.Id == campaign
                            select m.Make + " " + m.Model + " " + v.Chassis + " " + v.Engine + " " + v.Era).SingleAsync(ct);
        var queries = db.Queries.Where(q => q.CampaignId == campaign);
        var topics = await db.Topics.Where(t => t.CampaignId == campaign).Select(t => new
        {
            t.Key, Completed = queries.Count(q => q.TopicId == t.Id && q.State == WorkState.Done)
        }).ToListAsync(ct);
        var counts = topics.ToDictionary(t => t.Key, _ => 0);
        var facts = db.Claims.Where(c => c.CampaignId == campaign && c.State != Decision.Rejected &&
            db.Evidence.Any(e => e.ClaimId == c.Id));
        await foreach (var fact in facts.Select(c => new { c.Kind, c.Subject, c.Attribute,
            Japanese = db.Evidence.Any(e => e.ClaimId == c.Id && e.Version.Language == "ja") }).AsAsyncEnumerable().WithCancellation(ct))
        {
            counts[GapDiscovery.Topic(fact.Kind)]++;
            if (fact.Japanese) counts["japanese"]++;
            foreach (var system in VehicleSystem.All.Where(s => s.Covers(fact.Subject, fact.Attribute)))
                counts[system.Key]++;
        }
        var plan = topics.Select(t => new PlanCategory(t.Key, counts[t.Key], t.Completed))
            .OrderBy(t => t.GroundedClaims).ThenBy(t => t.CompletedSearches).ThenBy(t => t.Key).ToArray();
        var prior = await queries.Where(q => q.State == WorkState.Done || q.GapReason != null)
            .OrderByDescending(q => q.DueAt).ThenBy(q => q.Id).Select(q => q.Text).Take(32).ToListAsync(ct);
        var leads = await facts.OrderByDescending(c => c.Conflict).ThenByDescending(c => c.PartNumber != null)
            .ThenBy(c => c.Id).Take(16).Select(c => new { c.Subject, c.Attribute, c.Value, c.PartNumber, c.RelatedPartNumber,
                c.Applicability, c.Conflict, c.PossibleMatch }).ToListAsync(ct);
        var refs = await db.Resources.Where(r => r.CampaignId == campaign && r.ParentId != null)
            .OrderByDescending(r => r.Relevance).ThenByDescending(r => r.DiscoveredAt).Select(r => r.Title).Take(12).ToListAsync(ct);
        var remaining = limit - await queries.CountAsync(q => q.GapReason != null, ct);
        return new ResearchBrief(plan, prior, leads.Select(l => $"{l.Subject} {l.Attribute} {l.Value} {l.PartNumber} {l.RelatedPartNumber} {l.Applicability} (conflict={l.Conflict}, possible match={l.PossibleMatch})").ToArray(),
            refs, Math.Clamp(remaining, 0, 2)) { Target = target };
    }

    internal async Task QueueAsync(ResearchDb db, Guid campaign, VehicleVariant variant,
        IReadOnlyList<ProposedSearch> proposals, CancellationToken ct)
    {
        if (ProposalAllowance == 0 || proposals.Count == 0) return;
        var topics = await db.Topics.Where(t => t.CampaignId == campaign).ToDictionaryAsync(t => t.Key, ct);
        var known = (await db.Queries.Where(q => q.CampaignId == campaign).Select(q => q.Text).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = ProposalAllowance;
        foreach (var p in proposals.OrderBy(p => Categories.FirstOrDefault(t => t.Key == p.Category)?.GroundedClaims ?? int.MaxValue))
        {
            var query = string.Join(' ', p.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!topics.TryGetValue(p.Category, out var topic) ||
                !(Relevance.Mentions(query, variant.Chassis) || Relevance.Mentions(query, variant.Engine)) || !known.Add(query)) continue;
            var coverage = Categories.Single(c => c.Key == p.Category);
            db.Queries.Add(new SearchQuery { CampaignId = campaign, TopicId = topic.Id, DiscoveryProvider = "codex",
                Language = p.Language, Text = query, GapReason = p.Reason,
                Priority = 2 + Math.Min(25, coverage.GroundedClaims + coverage.CompletedSearches) });
            if (--remaining == 0) break;
        }
    }
}

