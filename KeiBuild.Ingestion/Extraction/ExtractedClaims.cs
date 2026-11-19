using KeiBuild.Catalog;

namespace KeiBuild.Ingestion.Extraction;

// The model can select evidence but cannot assign the URL, publisher, classification, or conditions.
internal sealed record ExtractedClaims(ClaimDraft?[] Claims);

internal sealed class ClaimDraft
{
    public string? ChassisCode { get; init; }
    public EntryKind? Kind { get; init; }
    public string? Title { get; init; }
    public string? Manufacturer { get; init; }
    public string? Category { get; init; }
    public string? PartNumber { get; init; }
    public string? OemReference { get; init; }
    public int FirstLine { get; init; }
    public int LastLine { get; init; }
    public int FitmentLine { get; init; }
}
