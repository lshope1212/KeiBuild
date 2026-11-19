namespace KeiBuild.Ingestion;

internal sealed record CommandLine(
    string Registry, string SourceId, string? File, string? FakeResponse, string Output, string? Model, bool SaveSnapshot)
{
    internal const string Usage = """
        KeiBuild.Ingestion processes one source document and exports candidates for manual review.

        Required: --registry <file.json> --source <registry-id>
        Input:    --file <document.html|.md|.txt> OR --url
        Options:  --fake-response <file.json>  Offline extractor for local documents
                  --model <model>             OpenAI model with Structured Outputs support
                  --output <new-directory>    Defaults to .local/ingestion/runs/<run-id>
                  --save-snapshot             Requires snapshotAllowed on the source
        Live extraction uses OPENAI_API_KEY and optionally KEIBUILD_OPENAI_MODEL. Default model: gpt-4o-mini.
        --url retrieves only the exact approved URL in the registry.
        """;

    private static readonly HashSet<string> ValueOptions =
        ["--registry", "--source", "--file", "--fake-response", "--output", "--model"];

    internal static CommandLine Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string? value = null;
            if (ValueOptions.Contains(name))
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new IngestionException($"Missing a value for {name}.");
                value = args[i];
            }
            else if (name is not ("--url" or "--save-snapshot"))
                throw new IngestionException($"Unknown argument {name}. Run with --help for usage.");

            if (!options.TryAdd(name, value))
                throw new IngestionException($"Duplicate argument {name}.");
        }

        var registry = options.GetValueOrDefault("--registry");
        var source = options.GetValueOrDefault("--source");
        var file = options.GetValueOrDefault("--file");
        var url = options.ContainsKey("--url");
        var fakeResponse = options.GetValueOrDefault("--fake-response");
        var model = options.GetValueOrDefault("--model");
        if (registry is null || source is null || url == (file is not null))
            throw new IngestionException("Specify --registry, --source, and exactly one of --file or --url.");

        if (fakeResponse is not null && (url || model is not null))
            throw new IngestionException("--fake-response requires a local --file and cannot be combined with --model.");

        var output = options.GetValueOrDefault("--output") ??
                     Path.Combine(".local", "ingestion", "runs", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        return new CommandLine(registry, source, file, fakeResponse, output, model, options.ContainsKey("--save-snapshot"));
    }
}
