namespace KeiBuild.Catalog;

public enum EntryKind { Part, Modification }

public enum SourceKind { Manufacturer, Vendor, Community }

public sealed record SourceReference(
    string Title, string Url, SourceKind Kind, string Locator, string Evidence);

public sealed record FitmentEntry(
    string ChassisCode, EntryKind Kind, string Title, string Manufacturer, string Category,
    string Conditions, SourceReference Source, string? PartNumber = null, string? OemReference = null);
