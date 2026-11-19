using System.Text;
using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Ingestion.Pipeline;
using KeiBuild.Ingestion.Sources;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
var ct = cancellation.Token;

try
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var command = CommandLine.Parse(args);
    var source = await SourceDefinition.LoadAsync(command.Registry, command.SourceId, ct);
    if (command.SaveSnapshot && !source.SnapshotAllowed)
        throw new IngestionException("Snapshot storage is not permitted by the source registry.");
    if (Path.Exists(command.Output))
        throw new IngestionException("The output path already exists. Choose a new run directory.");

    var extractor = CreateExtractor(command);
    var chassisCodes = await LoadChassisCodesAsync(ct);
    CollectedDocument document;
    if (command.File is { } file)
    {
        document = await DocumentBytes.CollectFileAsync(file, source, ct);
    }
    else
    {
        using var client = HttpDocumentCollector.CreateClient();
        var collector = new HttpDocumentCollector(client, Path.Combine(".local", "ingestion", "http"));
        document = await collector.CollectAsync(source, ct);
    }

    var result = await new IngestionPipeline(chassisCodes, extractor).RunAsync(source, document, ct);
    var path = await RunExport.WriteAsync(command.Output, result, document, command.SaveSnapshot, ct);
    var stats = result.Provenance;
    Console.WriteLine($"{stats.Accepted} candidate(s), {stats.Duplicates} duplicate(s), {stats.Withheld.Count} withheld.");
    foreach (var withheld in stats.Withheld)
        Console.WriteLine(withheld.Reason);
    Console.WriteLine(path);
    Console.WriteLine(stats.Accepted > 0
        ? "Paste candidates.json into the KeiBuild Review inbox. Review every listing against the source."
        : "The result is empty. There is nothing to submit to the inbox.");
    return 0;
}
catch (OperationCanceledException) when (ct.IsCancellationRequested)
{
    Console.Error.WriteLine("Ingestion cancelled.");
    return 130;
}
catch (Exception error) when (error is IngestionException or CatalogImportException or IOException or
                              UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine($"Ingestion failed: {error.Message}");
    return 1;
}

static IClaimExtractor CreateExtractor(CommandLine command)
{
    if (command.FakeResponse is { } path)
        return new FileClaimExtractor(path);

    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
        throw new IngestionException("Set OPENAI_API_KEY for live extraction, or use --fake-response with a local fixture.");

    var configuredModel = Environment.GetEnvironmentVariable("KEIBUILD_OPENAI_MODEL");
    var model = command.Model ?? (string.IsNullOrWhiteSpace(configuredModel) ? "gpt-4o-mini" : configuredModel.Trim());
    return new OpenAiClaimExtractor(model, key.Trim());
}

static async Task<string[]> LoadChassisCodesAsync(CancellationToken ct)
{
    var catalog = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
    await using var stream = File.OpenRead(catalog);
    try
    {
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var codes = json.RootElement.GetProperty("chassis").EnumerateArray()
            .Select(chassis => chassis.GetProperty("code").GetString()).ToArray();
        if (codes.Length == 0 || codes.Any(string.IsNullOrWhiteSpace))
            throw new IngestionException("The bundled catalog needs at least one nonblank chassis code.");
        return codes.Select(code => code!).ToArray();
    }
    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
    {
        throw new IngestionException("The bundled catalog is missing a valid chassis list. Rebuild after checking KeiBuild/Data/catalog.json.", error);
    }
}
