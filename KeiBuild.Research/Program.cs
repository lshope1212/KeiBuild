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
    if (args is ["codex-check"])
    {
        await new CodexKnowledgeExtractor().CheckAsync(CancellationToken.None);
        Console.WriteLine("ChatGPT login and extraction sandbox verified.");
        return 0;
    }
    var command = CommandLine.Parse(args);
    if (command.Command == "plan" || command.Has("--dry-run"))
    {
        var target = await ResearchTarget.ReadAsync(command.Get("--target-file"), CancellationToken.None);
        Print(new { command.Campaign, target.Chassis, command.SearchProvider, command.Provider, command.MaxMinutes, command.Limits,
            Categories = ResearchTarget.Topics.Select(t => t.Key).Concat(VehicleSystem.All.Select(s => s.Key)), Queries = target.Queries().ToArray() });
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

    if (command.Command == "stop")
    {
        var stopped = await db.Campaigns.Where(c => c.Name == command.Campaign)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.StopRequested, true), ct);
        if (stopped == 0) throw new IngestionException("Campaign not found.");
        Console.WriteLine("Stop requested. The worker finishes its active batch and releases the database lease.");
        return;
    }
    if (command.Command is "status" or "sources" or "claims" or "jobs" or "coverage" or "summary" or "source-review")
    {
        var readCampaign = await store.FindAsync(command.Campaign, ct);
        if (command.Command == "summary")
        {
            var summary = new { Status = await report.StatusAsync(readCampaign, ct), Coverage = await report.CoverageAsync(readCampaign.Id, ct) };
            await CampaignStore.WriteNewAsync(command.Need("--output"), JsonSerializer.Serialize(summary, CatalogImport.JsonOptions), ct);
            return;
        }
        Print(command.Command switch
        {
            "jobs" => await report.JobsAsync(readCampaign.Id, command.Offset, ct),
            "coverage" => await report.CoverageAsync(readCampaign.Id, ct),
            "source-review" => await report.SourcesAsync(readCampaign.Id, command.Offset, ct, reviewOnly: true),
            "sources" => await report.SourcesAsync(readCampaign.Id, command.Offset, ct),
            "claims" => await report.ClaimsAsync(readCampaign.Id, command.Offset, ct),
            _ => await report.StatusAsync(readCampaign, ct)
        });
        return;
    }

    await using var lease = await store.LockAsync(ct);
    var campaign = command.Command is "start" or "demo"
        ? await store.StartAsync(command.Campaign, command.Mode, ct, command.Get("--target-file") is { } path ? await ResearchTarget.ReadAsync(path, ct) : null)
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
        case "approve-source":
            if (campaign.Mode != CampaignMode.Live) throw new IngestionException("Choose a live campaign.");
            var origin = UrlRules.Normalize(command.Need("--scope")) ?? throw new IngestionException("Specify a public HTTPS scope.");
            var format = (command.Get("--format") ?? "html") switch
            {
                "html" => KeiBuild.Ingestion.Sources.DocumentFormat.Html,
                "text" => KeiBuild.Ingestion.Sources.DocumentFormat.Text,
                "markdown" => KeiBuild.Ingestion.Sources.DocumentFormat.Markdown,
                _ => throw new IngestionException("Choose html, text or markdown.")
            };
            await store.ApplyPoliciesAsync(campaign, [new("publisher-" + SourceEligibility.Key(origin.AbsoluteUri),
                origin.AbsoluteUri, true, command.Need("--publisher"), SourceKind.Community, format, command.Need("--basis"),
                true, true, true, false, false)], ct);
            Console.WriteLine($"Saved publisher approval for {origin.AbsoluteUri}.");
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
    string? pause;
    if (campaign.Mode == CampaignMode.Offline)
    {
        var fake = new FakeResearch();
        pause = await new CampaignSession(db, new CampaignRunner(db, store, fake, fake, fake), fake)
            .RunAsync(campaign, command, discover, work, ct);
    }
    else
    {
        using var searchHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        using var crawlHttp = HttpDocumentCollector.CreateClient();
        Func<string, string?> keys = Environment.GetEnvironmentVariable;
        var state = keys("KEIBUILD_RESEARCH_STATE_DIR") ?? Path.Combine(".local", "research", "http");
        var guardedHttp = new HttpDocumentCollector(crawlHttp, state);
        var variantModel = await (from variant in db.VehicleVariants join vehicle in db.VehicleModels on variant.VehicleModelId equals vehicle.Id
                                  where variant.Id == campaign.VehicleVariantId select new { Name = vehicle.Make + " " + vehicle.Model, variant.Engine }).SingleAsync(ct);
        ISearchProvider? search = !discover ? null : command.SearchProvider switch
        {
            "codex" => new CodexDiscoveryProvider(),
            "brave" => new BraveSearchProvider(searchHttp, ProviderKeys.Require(keys, "BRAVE_SEARCH_API_KEY")),
            "crossref" => new CrossrefSearch(guardedHttp, variantModel.Name, variantModel.Engine),
            _ => throw new IngestionException("Unsupported search provider.")
        };
        if (discover) Console.Error.WriteLine($"Discovery provider: {search!.GetType().Name}.");
        var model = keys("KEIBUILD_OPENAI_MODEL")?.Trim();
        IKnowledgeExtractor? ai = null;
        if (work && command.Limits.AiCalls > 0)
        {
            if (command.Provider == "codex") ai = new CodexKnowledgeExtractor();
            else if (command.Limits.BudgetUsd >= command.Limits.AiCostUsd)
                ai = new OpenAiKnowledgeExtractor(string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model,
                    ProviderKeys.Require(keys, "OPENAI_API_KEY"));
        }
        var collector = new ResearchHttpCollector(guardedHttp);
        pause = await new CampaignSession(db, new CampaignRunner(db, store, search, collector, ai), ai)
            .RunAsync(campaign, command, discover, work, ct);
    }
    Print(await report.StatusAsync(campaign, ct));
    if (pause != null) throw new IngestionException($"Campaign paused. {pause}");
}

static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(CatalogImport.JsonOptions)
{
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));
