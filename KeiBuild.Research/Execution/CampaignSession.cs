using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Extraction;
using Microsoft.EntityFrameworkCore;

namespace KeiBuild.Research.Execution;

internal sealed class CampaignSession(ResearchDb db, CampaignRunner runner, IKnowledgeExtractor? extractor)
{
    internal async Task<string?> RunAsync(ResearchCampaign campaign, CommandLine command, bool discover, bool work, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(command.MaxMinutes));
        var token = deadline.Token;
        var continuous = command.Has("--continuous");
        var remaining = command.Limits;
        var chassis = await db.VehicleVariants.Where(v => v.Id == campaign.VehicleVariantId).Select(v => v.Chassis).SingleAsync(token);
        await db.Campaigns.Where(c => c.Id == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.StopRequested, false), token);
        Console.Error.WriteLine($"Campaign {campaign.Name}, extraction {extractor?.Name ?? "disabled"}, runtime limit {command.MaxMinutes} minutes.");
        try
        {
            if (work)
            {
                var policies = await db.Policies.Where(p => p.CampaignId == campaign.Id).ToListAsync(token);
                await new CampaignStore(db).ReconcilePoliciesAsync(campaign.Id, policies, token);
            }
            var scanExistingClaims = true;
            while (true)
            {
                db.ChangeTracker.Clear();
                campaign = await db.Campaigns.SingleAsync(c => c.Id == campaign.Id, token);
                if (campaign.StopRequested) break;
                var batch = continuous ? remaining with { Searches = Math.Min(4, remaining.Searches),
                    Pages = Math.Min(4, remaining.Pages), AiCalls = Math.Min(4, remaining.AiCalls) } : remaining;
                var run = await runner.RunAsync(campaign, command.Command, batch, discover, work, token);
                if (run.StopReason == null && (run.Pages > 0 || scanExistingClaims))
                    await GapDiscovery.ExpandAsync(db, campaign.Id, chassis, remaining.ExpansionLimit, token);
                scanExistingClaims = false;
                Console.Error.WriteLine($"Batch {run.Id}: searches {run.Searches}, pages {run.Pages}, extractions {run.AiCalls}, estimated USD {run.EstimatedUsd}. {run.StopReason}");
                if (run.StopReason != null) return run.StopReason;
                if (!continuous) break;
                remaining = remaining with { Searches = remaining.Searches - run.Searches, Pages = remaining.Pages - run.Pages,
                    AiCalls = remaining.AiCalls - run.AiCalls, BudgetUsd = remaining.BudgetUsd - run.EstimatedUsd, RefreshHours = null };
                var next = await NextDueAsync(campaign, remaining, discover, work, token);
                if (next is null) break;
                // A stop request also wakes a session waiting for a site or provider cooldown.
                while (next.Value > DateTimeOffset.UtcNow)
                {
                    var delay = next.Value - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, delay.TotalMilliseconds)), token);
                    if (await db.Campaigns.Where(c => c.Id == campaign.Id).Select(c => c.StopRequested).SingleAsync(token)) return null;
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Console.Error.WriteLine(ct.IsCancellationRequested ? "Shutdown requested." : "Runtime limit reached.");
            db.ChangeTracker.Clear();
            await new CampaignStore(db).RecoverAsync(campaign.Id, remaining.Retries, cleanup.Token);
            ct.ThrowIfCancellationRequested();
        }
        return null;
    }

    private async Task<DateTimeOffset?> NextDueAsync(ResearchCampaign campaign, RunLimits limits, bool discover, bool work, CancellationToken ct)
    {
        var budget = new RunBudget(limits, new(), campaign.Mode == CampaignMode.Offline, extractor is CodexKnowledgeExtractor);
        var due = new List<DateTimeOffset>(2);
        if (discover && budget.CanSearch)
        {
            var search = await db.Queries.Where(q => q.CampaignId == campaign.Id && q.State == WorkState.Pending &&
                q.Attempts < limits.Retries).MinAsync(q => (DateTimeOffset?)q.DueAt, ct);
            if (search.HasValue) due.Add(campaign.SearchAfter > search ? campaign.SearchAfter.Value : search.Value);
        }
        if (work && budget.CanFetch)
        {
            var analyze = extractor is not null && budget.CanAnalyze;
            var crawl = await db.Jobs.Where(j => j.Resource.CampaignId == campaign.Id && j.State == WorkState.Pending &&
                j.Attempts < limits.Retries && (analyze || j.Resource.Kind == ResourceKind.Sitemap))
                .MinAsync(j => (DateTimeOffset?)j.DueAt, ct);
            if (crawl.HasValue) due.Add(campaign.ExtractionAfter > crawl ? campaign.ExtractionAfter.Value : crawl.Value);
        }
        return due.Count == 0 ? null : due.Min();
    }
}
