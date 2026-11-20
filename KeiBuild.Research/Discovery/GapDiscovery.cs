using System.Text;
using KeiBuild.Research.Data;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Discovery;

internal static class GapDiscovery
{
    internal const string ConflictReason = "Conflicting values need corroboration and human review";

    internal static string Topic(FactKind kind) => kind switch
    {
        FactKind.VehicleSpecification => "identity",
        FactKind.ServiceProcedure or FactKind.MaintenanceInterval => "maintenance",
        FactKind.MechanicalSpecification => "mechanical",
        FactKind.InstallationRequirement or FactKind.ModificationFitment or FactKind.Fitment => "modifications",
        FactKind.FailureSymptom or FactKind.FailureCause => "troubleshooting",
        _ => "oem-parts"
    };

    // Called under the writer lease. Search completion does not by itself close an evidence gap.
    internal static async Task ExpandAsync(ResearchDb db, Guid campaign, string chassis, int limit, CancellationToken ct)
    {
        if (limit == 0) return;
        var queries = db.Queries.Where(q => q.CampaignId == campaign);
        var remaining = limit - await queries.CountAsync(q => q.OriginClaimId != null, ct);
        if (remaining <= 0) return;
        var topics = await db.Topics.Where(t => t.CampaignId == campaign).ToDictionaryAsync(t => t.Key, t => t.Id, ct);
        var claims = db.Claims.AsNoTracking().Where(c => c.CampaignId == campaign && c.State != Decision.Rejected);
        var identifiers = claims.Where(c => c.Kind == FactKind.OemPartNumber || c.Kind == FactKind.PartIdentity);
        var known = (await queries.Select(q => q.Text).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = claims.Where(c => c.Conflict || c.PartNumber != null || c.RelatedPartNumber != null ||
            c.Kind == FactKind.ServiceProcedure || c.Kind == FactKind.MechanicalSpecification || c.Kind == FactKind.VehicleSpecification)
            .OrderByDescending(c => c.Conflict).ThenBy(c => c.Id);
        // Finish reading each page before issuing another query on the same connection.
        for (var offset = 0; remaining > 0; offset += 128)
        {
            var page = await candidates.Skip(offset).Take(128).ToListAsync(ct);
            foreach (var claim in page)
            {
                void Add(string topic, string terms, string reason, int priority, string? part = null)
                {
                    var words = $"{chassis} {terms}".Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    var text = string.Concat(string.Join(' ', words.Take(45)).EnumerateRunes().Take(380));
                    var language = text.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u4e00' and <= '\u9fff') ? "ja" : "en";
                    if (remaining == 0 || !known.Add(text)) return;
                    db.Queries.Add(new SearchQuery { CampaignId = campaign, TopicId = topics[topic], OriginClaimId = claim.Id,
                        Language = language, Priority = priority, Text = text, GapPartNumber = part, GapReason = reason });
                    remaining--;
                }
                if (claim.Conflict && claim.State == Decision.Pending)
                    Add(Topic(claim.Kind), $"{claim.Subject} {claim.Attribute} {claim.Applicability} factory specification",
                        ConflictReason, -3);
                foreach (var number in new[] { claim.RelatedPartNumber, claim.PartNumber }.OfType<string>().Distinct())
                    if (!await identifiers.AnyAsync(c => c.PartNumber == number, ct))
                        Add("oem-parts", $"{number} OEM parts catalog application", "Referenced part needs identification", -2, number);
                if (claim.Kind is FactKind.MechanicalSpecification or FactKind.ServiceProcedure or FactKind.VehicleSpecification &&
                    claim.State == Decision.Pending &&
                    await (from e in db.Evidence join v in db.Versions on e.VersionId equals v.Id
                           where e.ClaimId == claim.Id select v.ContentId).Distinct().Take(2).CountAsync(ct) < 2)
                    Add(Topic(claim.Kind), $"{claim.Subject} {claim.Attribute} {claim.Applicability} service manual",
                        "Technical claim has one distinct source text", 4);
                if (remaining == 0) break;
            }
            if (page.Count < 128) break;
        }
        await db.SaveChangesAsync(ct);
    }
}
