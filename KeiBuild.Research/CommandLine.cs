using System.Globalization;
using System.Text.RegularExpressions;
using KeiBuild.Ingestion;
using KeiBuild.Research.Data;
using KeiBuild.Research.Execution;

namespace KeiBuild.Research;

internal sealed record CommandLine(string Command, string Campaign, CampaignMode Mode, RunLimits Limits,
    IReadOnlyDictionary<string, string?> Values)
{
    public const string Help = """
        KeiBuild.Research — standalone HA4 research with PostgreSQL

        migrate
        plan [name]
        demo [name]                          Migrate and run the synthetic HA4 campaign
        start <name> [--offline]
        policies <name> --file <policies.json>
        discover <name> | work <name> | run <name> | resume <name>
        retry-failed <name>
        status <name> | sources <name> | claims <name> [--offset <n>]
        review <name> --claim <id> --decision approve|reject --note <text>
        export <name> --output <new-file.json> [--offset <n>]

        Run controls:
          --dry-run                 Print the HA4 plan and proposed limits without a database or providers
          --search-limit 0..50      Brave requests, default 8
          --page-limit 0..50        Page and sitemap attempts, default 8
          --ai-limit 0..20          Extraction requests, default 4
          --max-bytes 1024..524288  Document byte limit, default 524288
          --concurrency 1..4        Concurrent fetches, default 1
          --retries 1..5            Total attempts per item before explicit retry, default 3
          --budget-usd 0..20        Estimated API spending ceiling, default .25
          --search-cost-usd <n>     Estimated dollars per Brave call, default .01
          --ai-cost-usd <n>         Estimated dollars per OpenAI call, default .04
          --refresh-hours 1..8760   Recheck completed pages older than this age when requested

        Environment: KEIBUILD_RESEARCH_DB, BRAVE_SEARCH_API_KEY, OPENAI_API_KEY,
        optional KEIBUILD_OPENAI_MODEL and KEIBUILD_RESEARCH_STATE_DIR.
        """;

    private static readonly HashSet<string> Commands =
        ["migrate", "plan", "demo", "start", "policies", "discover", "work", "run", "resume", "retry-failed", "status", "sources", "claims", "review", "export"];
    private static readonly HashSet<string> Flags = ["--offline", "--dry-run"];
    private static readonly HashSet<string> RunArguments =
        ["--search-limit", "--page-limit", "--ai-limit", "--max-bytes", "--concurrency", "--retries",
         "--budget-usd", "--search-cost-usd", "--ai-cost-usd", "--refresh-hours", "--dry-run"];

    public bool Has(string flag) => Values.ContainsKey(flag);
    public string? Get(string key) => Values.GetValueOrDefault(key);
    public string Need(string key) => Get(key) ?? throw new IngestionException($"Specify {key}.");
    public int Offset => Integer(Get("--offset"), "--offset", 0, 0, int.MaxValue);

    public static CommandLine Parse(string[] args)
    {
        var name = args[0];
        if (!Commands.Contains(name))
            throw new IngestionException("Unknown research command. Use --help.");
        var index = 1;
        var hasName = index < args.Length && !args[index].StartsWith("--", StringComparison.Ordinal);
        if (!hasName && name is not ("migrate" or "plan" or "demo"))
            throw new IngestionException("Specify a campaign name.");
        if (hasName && name == "migrate")
            throw new IngestionException("migrate does not take a campaign name.");
        var campaign = hasName ? args[index++] : name == "demo" ? "ha4-demo" : "ha4";
        if (!Regex.IsMatch(campaign, "\\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new IngestionException("Use a campaign name of up to 64 letters, numbers, hyphens or underscores.");

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (index < args.Length)
        {
            var option = args[index++];
            var allow = name switch
            {
                "plan" or "demo" or "discover" or "work" or "run" or "resume" => RunArguments.Contains(option),
                "start" => option == "--offline",
                "policies" => option == "--file",
                "claims" or "sources" => option == "--offset",
                "review" => option is "--claim" or "--decision" or "--note",
                "export" => option is "--output" or "--offset",
                _ => false
            };
            if (!allow || values.ContainsKey(option))
                throw new IngestionException("Unknown or repeated option for this command. Use --help.");
            if (Flags.Contains(option))
                values.Add(option, null);
            else
            {
                if (index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index]))
                    throw new IngestionException($"Specify a value for {option}.");
                values.Add(option, args[index++]);
            }
        }
        int Number(string key, int fallback) => Integer(values.GetValueOrDefault(key), key, fallback, 0, int.MaxValue);
        decimal Money(string key, decimal fallback) => values.GetValueOrDefault(key) is not { } raw ? fallback :
            decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed :
            throw new IngestionException($"{key} must be a decimal dollar amount.");
        var limits = new RunLimits(Number("--search-limit", name == "demo" ? 14 : 8), Number("--page-limit", 8),
            Number("--ai-limit", name == "demo" ? 8 : 4), Number("--max-bytes", 524288), Number("--concurrency", 1),
            Number("--retries", 3), Money("--budget-usd", .25m), Money("--search-cost-usd", .01m), Money("--ai-cost-usd", .04m),
            values.ContainsKey("--refresh-hours") ? Number("--refresh-hours", 0) : null);
        limits.Validate();
        var mode = name == "demo" || values.ContainsKey("--offline") ? CampaignMode.Offline : CampaignMode.Live;
        return new CommandLine(name, campaign, mode, limits, values);
    }

    private static int Integer(string? raw, string key, int fallback, int minimum, int maximum) =>
        raw is null ? fallback :
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= minimum && number <= maximum
            ? number : throw new IngestionException($"{key} requires a nonnegative integer in range.");
}

public static class ProviderKeys
{
    public static string Require(Func<string, string?> lookup, string name) =>
        lookup(name)?.Trim() is { Length: > 0 } value ? value :
        throw new IngestionException($"Set {name} for this live command. Use demo for a credential-free campaign.");
}
