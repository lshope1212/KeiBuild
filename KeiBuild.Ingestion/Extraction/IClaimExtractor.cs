using KeiBuild.Ingestion.Collection;
using KeiBuild.Ingestion.Parsing;

namespace KeiBuild.Ingestion.Extraction;

public sealed record ExtractionInput(NormalizedDocument Document, IReadOnlyList<string> ChassisCodes, string? Language);

public interface IClaimExtractor
{
    string Name { get; }
    Task<string> ExtractAsync(ExtractionInput input, CancellationToken ct);
}

public sealed class FileClaimExtractor(string path) : IClaimExtractor
{
    public string Name => "Offline fixture";

    public async Task<string> ExtractAsync(ExtractionInput input, CancellationToken ct) =>
        DocumentBytes.Decode(await DocumentBytes.ReadFileAsync(path, ct), charset: null);
}
