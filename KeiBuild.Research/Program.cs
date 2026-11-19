using System.Text;
using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Research;
using KeiBuild.Research.Collection;
using KeiBuild.Research.Data;
using KeiBuild.Research.Discovery;
using KeiBuild.Research.Execution;
using KeiBuild.Research.Extraction;
using KeiBuild.Research.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

Console.OutputEncoding = new UTF8Encoding(false);
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine(CommandLine.Help);
    return 0;
}

try
{
    var command = CommandLine.Parse(args);
    if (command.Command == "plan" || command.Has("--dry-run"))
    {
        Print(new { command.Campaign, Chassis = Ha4Plan.Chassis, command.Limits,
            Queries = Ha4Plan.Queries().ToArray() });
        return 0;
    }

    var connection = ProviderKeys.Require(Environment.GetEnvironmentVariable, "KEIBUILD_RESEARCH_DB");
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddScoped(_ => new ResearchDb(ResearchDb.Options(connection)));
    builder.Services.AddScoped<CampaignStore>();
    builder.Services.AddScoped<CampaignReport>();
    using var host = builder.Build();
    await host.StartAsync();
    try
    {
        var ct = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        await using var scope = host.Services.CreateAsyncScope();
        await ExecuteAsync(scope.ServiceProvider, command, ct);
    }
    finally
    {
        await host.StopAsync();
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Research stopped. Resume the campaign to recover interrupted work.");
    return 130;
}
catch (PostgresException error)
{
    Console.Error.WriteLine($"PostgreSQL rejected the operation (SQLSTATE {error.SqlState}). Check the connection, schema migration and database permissions.");
    return 1;
}
catch (NpgsqlException)
{
    Console.Error.WriteLine("Could not complete the PostgreSQL operation. Check KEIBUILD_RESEARCH_DB and that PostgreSQL is reachable.");
    return 1;
}
catch (Exception error) when (error is IngestionException or CatalogImportException or IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

static async Task ExecuteAsync(IServiceProvider services, CommandLine command, CancellationToken ct)
{
    var db = services.GetRequiredService<ResearchDb>();
    var store = services.GetRequiredService<CampaignStore>();
    var report = services.GetRequiredService<CampaignReport>();
    if (command.Command is "migrate" or "demo")
    {
        await db.Database.MigrateAsync(ct);
        if (command.Command == "migrate")
        {
            Console.WriteLine("Research database migrations applied.");
            return;
        }
    }

    if (command.Command is "status" or "sources" or "claims")
    {
        var readCampaign = await store.FindAsync(command.Campaign, ct);
        Print(command.Command switch
        {
            "sources" => await report.SourcesAsync(readCampaign.Id, command.Offset, ct),
            "claims" => await report.ClaimsAsync(readCampaign.Id, command.Offset, ct),
            _ => await report.StatusAsync(readCampaign, ct)
        });
        return;
    }

    await using var lease = await store.LockAsync(ct);
    var campaign = command.Command is "start" or "demo"
        ? await store.StartAsync(command.Campaign, command.Mode, ct)
        : await store.FindAsync(command.Campaign, ct);
    switch (command.Command)
    {
        case "start":
        case "demo":
            if (campaign.Mode == CampaignMode.Offline)
                await store.ApplyPoliciesAsync(campaign, [FakeResearch.Policy], ct);
            if (command.Command == "start")
                Print(await report.StatusAsync(campaign, ct));
            else
                await RunAsync(db, store, report, campaign, command, ct);
            break;
        case "policies":
            if (campaign.Mode != CampaignMode.Live)
                throw new IngestionException("The synthetic campaign uses the bundled fixture policy.");
            await store.ApplyPoliciesAsync(campaign, await PolicyRules.ReadAsync(command.Need("--file"), ct), ct);
            Print(await report.SourcesAsync(campaign.Id, 0, ct));
            break;
        case "review":
            var note = command.Need("--note");
            if (!Guid.TryParse(command.Need("--claim"), out var claim) || note.Length > 2_000 || note.Contains('\0'))
                throw new IngestionException("Review needs a claim UUID and a note of up to 2,000 characters.");
            var decision = command.Need("--decision") switch
            {
                "approve" => Decision.Approved,
                "reject" => Decision.Rejected,
                _ => throw new IngestionException("Use --decision approve or --decision reject.")
            };
            await store.ReviewAsync(campaign, claim, decision, note, ct);
            Console.WriteLine($"Claim {claim} {decision.ToString().ToLowerInvariant()}.");
            break;
        case "export":
            var output = command.Need("--output");
            var exported = await store.ExportAsync(campaign, output, command.Offset, ct);
            Console.WriteLine($"Exported {exported} reviewed fitments to {Path.GetFullPath(output)}. Next offset {command.Offset + exported}.");
            break;
        case "retry-failed":
            Console.WriteLine($"Requeued {await store.RetryFailedAsync(campaign.Id, ct)} failed items.");
            break;
        default:
            await RunAsync(db, store, report, campaign, command, ct);
            break;
    }
}

static async Task RunAsync(ResearchDb db, CampaignStore store, CampaignReport report, ResearchCampaign campaign,
    CommandLine command, CancellationToken ct)
{
    var discover = command.Command != "work" && command.Limits.Searches > 0;
    var work = command.Command != "discover" && command.Limits.Pages > 0;
    if (campaign.Mode == CampaignMode.Offline)
    {
        var fake = new FakeResearch();
        await new CampaignRunner(db, store, fake, fake, fake)
            .RunAsync(campaign, command.Command, command.Limits, discover, work, ct);
    }
    else
    {
        using var searchHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var crawlHttp = HttpDocumentCollector.CreateClient();
        Func<string, string?> keys = Environment.GetEnvironmentVariable;
        var search = discover ? new BraveSearchProvider(searchHttp, ProviderKeys.Require(keys, "BRAVE_SEARCH_API_KEY")) : null;
        var model = keys("KEIBUILD_OPENAI_MODEL")?.Trim();
        var ai = work && command.Limits.AiCalls > 0 && command.Limits.BudgetUsd >= command.Limits.AiCostUsd
            ? new OpenAiKnowledgeExtractor(string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                ProviderKeys.Require(keys, "OPENAI_API_KEY")) : null;
        var state = keys("KEIBUILD_RESEARCH_STATE_DIR") ?? Path.Combine(".local", "research", "http");
        var collector = new ResearchHttpCollector(new HttpDocumentCollector(crawlHttp, state));
        await new CampaignRunner(db, store, search, collector, ai)
            .RunAsync(campaign, command.Command, command.Limits, discover, work, ct);
    }
    Print(await report.StatusAsync(campaign, ct));
}

static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(CatalogImport.JsonOptions)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));
