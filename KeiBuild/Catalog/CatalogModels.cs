namespace KeiBuild.Catalog;

public sealed record Chassis(
    string Code, string Make, string Model, int Introduced, string Engine,
    string Drive, string Summary, SourceReference Source)
{
    public string Name => $"{Make} {Model}";
}

public enum ReviewStatus { Pending, Approved, Rejected }

public sealed record ReviewItem(
    Guid Id, FitmentEntry Entry, DateTimeOffset SubmittedAt, ReviewStatus Status,
    DateTimeOffset? ReviewedAt = null);
