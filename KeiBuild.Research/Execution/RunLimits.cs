using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Research.Data;

namespace KeiBuild.Research.Execution;

public sealed record RunLimits(int Searches = 8, int Pages = 8, int AiCalls = 4,
    int MaximumBytes = DocumentBytes.MaxBytes, int Concurrency = 1, int Retries = 3,
    decimal BudgetUsd = 0.25m, decimal SearchCostUsd = 0.01m, decimal AiCostUsd = 0.04m,
    int? RefreshHours = null, int ExpansionLimit = 32)
{
    public void Validate(bool continuous = false)
    {
        if (Searches < 0 || Searches > (continuous ? 10000 : 50) || Pages < 0 || Pages > (continuous ? 10000 : 50) ||
            AiCalls < 0 || AiCalls > (continuous ? 10000 : 20) ||
            MaximumBytes is < 1024 or > DocumentBytes.MaxBytes || Concurrency is < 1 or > 4 ||
            Retries is < 1 or > 5 || BudgetUsd is < 0 or > 20 ||
            SearchCostUsd is < 0.000001m or > 20m || AiCostUsd is < 0.000001m or > 20m ||
            RefreshHours is < 1 or > 8_760 || ExpansionLimit is < 0 or > 500)
            throw new IngestionException("Research limits are out of range. See --help for supported bounds.");
    }
}

public sealed class RunBudget(RunLimits limits, ResearchRun run, bool offline, bool codex = false, bool freeSearch = false)
{
    public bool CanSearch => run.Searches < limits.Searches && CanSpend(freeSearch ? 0 : limits.SearchCostUsd);
    public bool CanFetch => run.Pages < limits.Pages;
    public bool CanAnalyze => run.AiCalls < limits.AiCalls && CanSpend(codex ? 0 : limits.AiCostUsd);

    public bool ReserveSearch()
    {
        if (!CanSearch)
            return false;
        run.Searches++;
        Spend(freeSearch ? 0 : limits.SearchCostUsd);
        return true;
    }

    public bool ReservePage()
    {
        if (!CanFetch)
            return false;
        run.Pages++;
        return true;
    }

    public bool ReserveAnalysis()
    {
        if (!CanAnalyze)
            return false;
        run.AiCalls++;
        Spend(codex ? 0 : limits.AiCostUsd);
        return true;
    }

    private bool CanSpend(decimal amount) => offline || run.EstimatedUsd + amount <= limits.BudgetUsd;
    private void Spend(decimal amount) => run.EstimatedUsd += offline ? 0 : amount;
}

internal static class RetryPolicy
{
    internal static DateTimeOffset Due(int attempts, DateTimeOffset? providerTime)
    {
        var backoff = DateTimeOffset.UtcNow.AddSeconds(15 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 5)));
        return providerTime is { } retryAt && retryAt > backoff ? retryAt : backoff;
    }
}
