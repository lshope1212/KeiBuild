namespace KeiBuild.Catalog;

public enum EntryKind { Part, Modification }

public enum SourceKind { Manufacturer, Vendor, Community }

public sealed record SourceReference(
    string Title, string Url, SourceKind Kind, string Locator, string Evidence);

public sealed record Chassis(
    string Code, string Make, string Model, int Introduced, string Engine,
    string Drive, string Summary, SourceReference Source)
{
    public string Name => $"{Make} {Model}";
}

public sealed record FitmentEntry(
    string ChassisCode, EntryKind Kind, string Title, string Manufacturer, string Category,
    string Conditions, SourceReference Source, string? PartNumber = null, string? OemReference = null);

public enum ReviewStatus { Pending, Approved, Rejected }

public sealed record ReviewItem(
    Guid Id, FitmentEntry Entry, DateTimeOffset SubmittedAt, ReviewStatus Status,
    DateTimeOffset? ReviewedAt = null);
