using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Ingestion.Pipeline;

public static class RunExport
{
    public static async Task<string> WriteAsync(string output, IngestionResult result, CollectedDocument document,
        bool saveSnapshot, CancellationToken ct)
    {
        if (saveSnapshot && !result.Provenance.Source.SnapshotAllowed)
            throw new IngestionException("Snapshot storage is not permitted by the source registry.");

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
        if (Path.Exists(destination))
            throw new IngestionException("The output path already exists. Choose a new run directory.");

        var parent = Path.GetDirectoryName(destination)
            ?? throw new IngestionException("Choose an output subdirectory instead of a drive root.");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".keibuild-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(staging);
        var candidatePath = Path.Combine(staging, "candidates.json");
        var provenancePath = Path.Combine(staging, "provenance.json");
        var snapshotPath = Path.Combine(staging, "source" + Extension(result.Provenance.Source.Format));
        try
        {
            await File.WriteAllTextAsync(candidatePath, result.CandidateJson, ct);
            await File.WriteAllTextAsync(provenancePath,
                JsonSerializer.Serialize(result.Provenance, CatalogImport.JsonOptions), ct);
            if (saveSnapshot)
                await File.WriteAllBytesAsync(snapshotPath, document.Bytes, ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                File.Delete(candidatePath);
                File.Delete(provenancePath);
                File.Delete(snapshotPath);
                Directory.Delete(staging);
            }
        }

        return Path.Combine(destination, "candidates.json");
    }

    private static string Extension(DocumentFormat format) => format switch
    {
        DocumentFormat.Html => ".html",
        DocumentFormat.Markdown => ".md",
        _ => ".txt"
    };
}
