using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Collection;

namespace KeiBuild.Ingestion.Sources;

public enum DocumentFormat { Html, Markdown, Text }

public sealed record SourceDefinition(
    string Id, string Publisher, string Title, string Url, SourceKind Kind, DocumentFormat Format,
    bool AutomatedRetrievalApproved, string PermissionNotes, bool SnapshotAllowed,
    string? Language = null, DateTimeOffset? LastRetrievedAtUtc = null)
{
    public Uri Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 ||
            string.IsNullOrWhiteSpace(Publisher) || Publisher.Length > 150 ||
            string.IsNullOrWhiteSpace(Title) || Title.Length > 150 ||
            string.IsNullOrWhiteSpace(PermissionNotes) || PermissionNotes.Length > 2_000)
        {
            throw new IngestionException("The source needs an ID, publisher, title, and permission or reuse notes.");
        }

        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Format) || Language?.Length > 40)
            throw new IngestionException("Use a supported source type, document format, and language tag.");

        if (!CatalogImport.TrySourceUrl(Url, out var uri))
            throw new IngestionException("The source needs an absolute HTTP(S) attribution URL without credentials.");

        return uri;
    }

    public static async Task<SourceDefinition> LoadAsync(string path, string sourceId, CancellationToken ct)
    {
        var bytes = await DocumentBytes.ReadFileAsync(path, ct);
        SourceDefinition?[]? entries;
        try
        {
            entries = JsonSerializer.Deserialize<SourceDefinition?[]>(bytes, CatalogImport.JsonOptions);
        }
        catch (JsonException error)
        {
            throw new IngestionException($"Invalid source registry JSON at {error.Path ?? "$"}.", error);
        }

        if (entries is null || entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Id)))
            throw new IngestionException("The registry must contain a JSON array of sources with IDs.");

        var matches = entries.OfType<SourceDefinition>().Where(entry => entry.Id == sourceId).ToArray();
        if (matches.Length != 1)
            throw new IngestionException("The registry must contain exactly one source with the requested ID.");

        matches[0].Validate();
        return matches[0];
    }
}
