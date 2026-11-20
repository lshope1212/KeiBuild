using KeiBuild.Catalog;
using KeiBuild.Ingestion.Sources;

namespace KeiBuild.Research.Data;

public enum CampaignMode { Offline, Live }
public enum WorkState { Pending, Running, Done, Failed, Blocked }
public enum ResourceKind { Technical, Manufacturer, Vendor, Community, Irrelevant, Sitemap }
public enum FactKind
{
    VehicleSpecification, PartIdentity, OemPartNumber, AftermarketPartNumber, PartCrossReference,
    Supersession, MechanicalSpecification, ServiceProcedure, MaintenanceInterval, InstallationRequirement,
    ModificationFitment, FailureSymptom, FailureCause, CommunityExperience, Fitment
}
public enum Decision { Pending, Approved, Rejected }

public sealed class VehicleModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class VehicleVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VehicleModelId { get; set; }
    public string Chassis { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Era { get; set; } = "";
}

public sealed class ResearchCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid VehicleVariantId { get; set; }
    public bool StopRequested { get; set; }
    public DateTimeOffset? SearchAfter { get; set; }
    public DateTimeOffset? ExtractionAfter { get; set; }
    public CampaignMode Mode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ResearchTopic
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
}

public sealed class SearchQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid TopicId { get; set; }
    public string Language { get; set; } = "";
    public string Text { get; set; } = "";
    public Guid? OriginClaimId { get; set; }
    public string? GapPartNumber { get; set; }
    public string? GapReason { get; set; }
    public int Priority { get; set; }
    public WorkState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ResearchRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string? Provider { get; set; }
    public string? StopReason { get; set; }
    public string Command { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int Searches { get; set; }
    public int Pages { get; set; }
    public int AiCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal EstimatedUsd { get; set; }
    public int DuplicateUrls { get; set; }
    public int DuplicateDocuments { get; set; }
    public int DuplicateClaims { get; set; }
    public int WithheldClaims { get; set; }
}

public sealed class SearchAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid QueryId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int? Returned { get; set; }
    public string? Error { get; set; }
}

public sealed class DiscoveredResource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public Guid? SearchQueryId { get; set; }
    public Guid? ParentId { get; set; }
    public string Url { get; set; } = "";
    public string Host { get; set; } = "";
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string? Language { get; set; }
    public ResourceKind Kind { get; set; }
    public int Relevance { get; set; }
    public DateTimeOffset DiscoveredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRetrievedAt { get; set; }
}

public sealed class SourcePolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string Key { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool Prefix { get; set; }
    public string Publisher { get; set; } = "";
    public SourceKind Kind { get; set; }
    public DocumentFormat Format { get; set; }
    public string? Language { get; set; }
    public string PermissionNotes { get; set; } = "";
    public bool AllowFetch { get; set; }
    public bool AllowAi { get; set; }
    public bool AllowEvidence { get; set; }
    public bool AllowSnapshot { get; set; }
    public bool ExploreLinks { get; set; }
    public string? SitemapUrl { get; set; }
    public int DelaySeconds { get; set; } = 10;
}

public sealed class CrawlJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public DiscoveredResource Resource { get; set; } = null!;
    public WorkState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset DueAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CrawlAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid JobId { get; set; }
    public Guid? PolicyId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public int? HttpStatus { get; set; }
    public Guid? VersionId { get; set; }
    public bool AiReserved { get; set; }
    public string? Error { get; set; }
}

// Content identity is scoped by campaign and format. Versions keep URL-specific attribution.
public sealed class DocumentContent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string Sha256 { get; set; } = "";
    public DocumentFormat Format { get; set; }
    public string EncodingKey { get; set; } = "";
    public string? AnalysisJson { get; set; }
    public DateTimeOffset? AnalyzedAt { get; set; }
}

public sealed class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourceId { get; set; }
    public Guid ContentId { get; set; }
    public Guid PolicyId { get; set; }
    public string Publisher { get; set; } = "";
    public SourceKind SourceKind { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int SizeBytes { get; set; }
    public byte[]? Snapshot { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public sealed class KnowledgeClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CampaignId { get; set; }
    public string ClaimKey { get; set; } = "";
    public string GroupKey { get; set; } = "";
    public FactKind Kind { get; set; }
    public string Subject { get; set; } = "";
    public string Attribute { get; set; } = "";
    public string Value { get; set; } = "";
    public string? Unit { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public string? PartNumberKey { get; set; }
    public string? RelatedPartNumber { get; set; }
    public string? Applicability { get; set; }
    public bool Conflict { get; set; }
    public bool PossibleMatch { get; set; }
    public Decision State { get; set; }
}

public sealed class ClaimEvidence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public Guid VersionId { get; set; }
    public DocumentVersion Version { get; set; } = null!;
    public string Locator { get; set; } = "";
    public string Passage { get; set; } = "";
    public string? EnglishTranslation { get; set; }
    public string? FitmentJson { get; set; }
}

public sealed class ReviewDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public Decision State { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CatalogPart
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Manufacturer { get; set; } = "";
    public string ManufacturerKey { get; set; } = "";
    public string PartNumberKey { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class PartIdentifier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PartId { get; set; }
    public string Number { get; set; } = "";
    public FactKind Kind { get; set; }
}

public sealed class ApprovedKnowledge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClaimId { get; set; }
    public Guid VehicleVariantId { get; set; }
    public Guid? PartId { get; set; }
    public Guid DecisionId { get; set; }
    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
}
