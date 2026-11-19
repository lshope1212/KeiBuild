using System.Security.Cryptography;
using System.Text.Json;
using KeiBuild.Catalog;
using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Extraction;
using KeiBuild.Ingestion.Parsing;
using KeiBuild.Ingestion.Sources;
using KeiBuild.Ingestion.Validation;

namespace KeiBuild.Ingestion.Pipeline;

public sealed record WithheldClaim(int Row, string Reason);

public sealed record IngestionProvenance(
    SourceDefinition Source, DateTimeOffset CollectedAtUtc, DateTimeOffset? RetrievedAtUtc,
    string Sha256, string? DeclaredCharset, string InputMethod, string Extractor,
    int NormalizerVersion, int NormalizedLineCount, int Accepted, int Duplicates,
    IReadOnlyList<WithheldClaim> Withheld);

public sealed record IngestionResult(string CandidateJson, IngestionProvenance Provenance);

public sealed class IngestionPipeline(IReadOnlyList<string> chassisCodes, IClaimExtractor extractor)
{
    private readonly CatalogImport import = new(chassisCodes);

    public async Task<IngestionResult> RunAsync(SourceDefinition source, CollectedDocument document, CancellationToken ct)
    {
        var url = source.Validate();
        var normalized = DocumentNormalizer.Normalize(document, source.Format);
        var response = await extractor.ExtractAsync(new(normalized, chassisCodes, source.Language), ct);
        var drafts = Parse(response);
        var accepted = new List<FitmentEntry>();
        var withheld = new List<WithheldClaim>();
        var duplicates = 0;
        var seen = new HashSet<(string, EntryKind, string, string, string, string, string)>();
        for (var i = 0; i < drafts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidate = CandidateEvidence.Build(drafts[i], normalized, source, url, import, i + 1);
                if (seen.Add(CatalogImport.ClaimKey(candidate)))
                    accepted.Add(candidate);
                else
                    duplicates++;
            }
            catch (CatalogImportException error)
            {
                withheld.Add(new(i + 1, error.Message));
            }
        }

        var payload = import.Serialize(accepted);
        var retrieved = document.RetrievedFromUrl ? document.CollectedAtUtc : source.LastRetrievedAtUtc;
        var provenance = new IngestionProvenance(
            source with { Url = url.AbsoluteUri, LastRetrievedAtUtc = retrieved },
            document.CollectedAtUtc, retrieved, Convert.ToHexString(SHA256.HashData(document.Bytes)).ToLowerInvariant(),
            document.Charset, document.RetrievedFromUrl ? "URL" : "Local file", extractor.Name,
            NormalizerVersion: 1, normalized.Lines.Count, accepted.Count, duplicates, withheld);
        return new IngestionResult(payload, provenance);
    }

    private static ClaimDraft?[] Parse(string response)
    {
        if (string.IsNullOrWhiteSpace(response) || response.Length > 128_000)
            throw new IngestionException("The extractor response is empty or exceeds the response size limit.");
        try
        {
            var envelope = JsonSerializer.Deserialize<ExtractedClaims>(response, CatalogImport.JsonOptions);
            if (envelope?.Claims is null || envelope.Claims.Length > CatalogImport.MaxEntries)
                throw new IngestionException("The extractor must return an object with a claims array containing up to 50 entries.");
            return envelope.Claims;
        }
        catch (JsonException error)
        {
            throw new IngestionException($"Malformed extractor JSON or unsupported fields at {error.Path ?? "$"}.", error);
        }
    }
}
