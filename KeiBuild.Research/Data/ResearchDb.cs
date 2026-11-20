using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KeiBuild.Research.Data;

public sealed class ResearchDb(DbContextOptions<ResearchDb> options) : DbContext(options)
{
    public DbSet<VehicleModel> VehicleModels => Set<VehicleModel>();
    public DbSet<VehicleVariant> VehicleVariants => Set<VehicleVariant>();
    public DbSet<ResearchCampaign> Campaigns => Set<ResearchCampaign>();
    public DbSet<ResearchTopic> Topics => Set<ResearchTopic>();
    public DbSet<SearchQuery> Queries => Set<SearchQuery>();
    public DbSet<ResearchRun> Runs => Set<ResearchRun>();
    public DbSet<SearchAttempt> SearchAttempts => Set<SearchAttempt>();
    public DbSet<DiscoveredResource> Resources => Set<DiscoveredResource>();
    public DbSet<SourcePolicy> Policies => Set<SourcePolicy>();
    public DbSet<CrawlJob> Jobs => Set<CrawlJob>();
    public DbSet<CrawlAttempt> CrawlAttempts => Set<CrawlAttempt>();
    public DbSet<DocumentContent> Contents => Set<DocumentContent>();
    public DbSet<DocumentVersion> Versions => Set<DocumentVersion>();
    public DbSet<KnowledgeClaim> Claims => Set<KnowledgeClaim>();
    public DbSet<ClaimEvidence> Evidence => Set<ClaimEvidence>();
    public DbSet<ReviewDecision> Decisions => Set<ReviewDecision>();
    public DbSet<CatalogPart> Parts => Set<CatalogPart>();
    public DbSet<PartIdentifier> PartIdentifiers => Set<PartIdentifier>();
    public DbSet<ApprovedKnowledge> Approved => Set<ApprovedKnowledge>();

    public static DbContextOptions<ResearchDb> Options(string connectionString) =>
        new DbContextOptionsBuilder<ResearchDb>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "research"))
            .Options;

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("research");
        model.Entity<VehicleModel>().ToTable("VehicleModels", "catalog")
            .HasIndex(item => new { item.Make, item.Model }).IsUnique();
        model.Entity<VehicleVariant>().ToTable("VehicleVariants", "catalog")
            .HasIndex(item => new { item.VehicleModelId, item.Chassis, item.Engine }).IsUnique();
        Link<VehicleVariant, VehicleModel>(model, nameof(VehicleVariant.VehicleModelId));
        model.Entity<ResearchCampaign>().HasIndex(item => item.Name).IsUnique();
        Link<ResearchCampaign, VehicleVariant>(model, nameof(ResearchCampaign.VehicleVariantId));
        model.Entity<ResearchTopic>().HasIndex(item => new { item.CampaignId, item.Key }).IsUnique();
        Link<ResearchTopic, ResearchCampaign>(model, nameof(ResearchTopic.CampaignId));
        model.Entity<SearchQuery>().HasIndex(item => new { item.CampaignId, item.Language, item.Text }).IsUnique();
        model.Entity<SearchQuery>().HasIndex(item => new { item.CampaignId, item.State, item.DueAt });
        Link<SearchQuery, ResearchCampaign>(model, nameof(SearchQuery.CampaignId));
        Link<SearchQuery, KnowledgeClaim>(model, nameof(SearchQuery.OriginClaimId));
        Link<SearchQuery, ResearchTopic>(model, nameof(SearchQuery.TopicId));
        Link<ResearchRun, ResearchCampaign>(model, nameof(ResearchRun.CampaignId));
        model.Entity<ResearchRun>().Property(item => item.EstimatedUsd).HasPrecision(12, 6);
        model.Entity<SearchAttempt>().Property(item => item.CitationsJson).HasColumnType("jsonb");
        Link<SearchAttempt, ResearchRun>(model, nameof(SearchAttempt.RunId));
        Link<SearchAttempt, SearchQuery>(model, nameof(SearchAttempt.QueryId));
        model.Entity<DiscoveredResource>().Property(item => item.Url).HasMaxLength(2048);
        model.Entity<DiscoveredResource>().HasIndex(item => new { item.CampaignId, item.Url }).IsUnique();
        model.Entity<DiscoveredResource>().HasIndex(item => new { item.CampaignId, item.Host, item.Relevance });
        Link<DiscoveredResource, ResearchCampaign>(model, nameof(DiscoveredResource.CampaignId));
        Link<DiscoveredResource, SearchQuery>(model, nameof(DiscoveredResource.SearchQueryId));
        Link<DiscoveredResource, DiscoveredResource>(model, nameof(DiscoveredResource.ParentId));
        model.Entity<SourcePolicy>().HasIndex(item => new { item.CampaignId, item.Key }).IsUnique();
        Link<SourcePolicy, ResearchCampaign>(model, nameof(SourcePolicy.CampaignId));
        model.Entity<CrawlJob>().HasIndex(item => item.ResourceId).IsUnique();
        model.Entity<CrawlJob>().HasIndex(item => new { item.State, item.DueAt });
        model.Entity<CrawlJob>().HasOne(item => item.Resource).WithMany().HasForeignKey(item => item.ResourceId)
            .OnDelete(DeleteBehavior.Restrict);
        Link<CrawlAttempt, ResearchRun>(model, nameof(CrawlAttempt.RunId));
        Link<CrawlAttempt, CrawlJob>(model, nameof(CrawlAttempt.JobId));
        Link<CrawlAttempt, SourcePolicy>(model, nameof(CrawlAttempt.PolicyId));
        Link<CrawlAttempt, DocumentVersion>(model, nameof(CrawlAttempt.VersionId));
        model.Entity<DocumentContent>().HasIndex(item => new { item.CampaignId, item.Sha256, item.Format, item.EncodingKey }).IsUnique();
        model.Entity<DocumentContent>().Property(item => item.AnalysisJson).HasColumnType("jsonb");
        Link<DocumentContent, ResearchCampaign>(model, nameof(DocumentContent.CampaignId));
        model.Entity<DocumentVersion>().HasIndex(item => new { item.ResourceId, item.ContentId }).IsUnique();
        Link<DocumentVersion, DiscoveredResource>(model, nameof(DocumentVersion.ResourceId));
        Link<DocumentVersion, DocumentContent>(model, nameof(DocumentVersion.ContentId));
        Link<DocumentVersion, SourcePolicy>(model, nameof(DocumentVersion.PolicyId));
        model.Entity<KnowledgeClaim>().HasIndex(item => new { item.CampaignId, item.ClaimKey }).IsUnique();
        model.Entity<KnowledgeClaim>().HasIndex(item => new { item.CampaignId, item.GroupKey });
        model.Entity<KnowledgeClaim>().HasIndex(item => new { item.CampaignId, item.PartNumberKey });
        Link<KnowledgeClaim, ResearchCampaign>(model, nameof(KnowledgeClaim.CampaignId));
        model.Entity<ClaimEvidence>().HasIndex(item => new { item.ClaimId, item.VersionId, item.Locator }).IsUnique();
        model.Entity<ClaimEvidence>().Property(item => item.FitmentJson).HasColumnType("jsonb");
        model.Entity<ClaimEvidence>().HasOne(item => item.Version).WithMany().HasForeignKey(item => item.VersionId)
            .OnDelete(DeleteBehavior.Restrict);
        Link<ClaimEvidence, KnowledgeClaim>(model, nameof(ClaimEvidence.ClaimId));
        Link<ReviewDecision, KnowledgeClaim>(model, nameof(ReviewDecision.ClaimId));
        model.Entity<CatalogPart>().ToTable("Parts", "catalog")
            .HasIndex(item => new { item.ManufacturerKey, item.PartNumberKey }).IsUnique();
        model.Entity<PartIdentifier>().ToTable("PartIdentifiers", "catalog")
            .HasIndex(item => new { item.PartId, item.Number, item.Kind }).IsUnique();
        Link<PartIdentifier, CatalogPart>(model, nameof(PartIdentifier.PartId));
        model.Entity<ApprovedKnowledge>().ToTable("ApprovedKnowledge", "catalog")
            .HasIndex(item => item.ClaimId).IsUnique();
        Link<ApprovedKnowledge, KnowledgeClaim>(model, nameof(ApprovedKnowledge.ClaimId));
        Link<ApprovedKnowledge, VehicleVariant>(model, nameof(ApprovedKnowledge.VehicleVariantId));
        Link<ApprovedKnowledge, CatalogPart>(model, nameof(ApprovedKnowledge.PartId));
        Link<ApprovedKnowledge, ReviewDecision>(model, nameof(ApprovedKnowledge.DecisionId));
    }

    private static void Link<TChild, TParent>(ModelBuilder model, string key)
        where TChild : class where TParent : class =>
        model.Entity<TChild>().HasOne<TParent>().WithMany().HasForeignKey(key).OnDelete(DeleteBehavior.Restrict);
}

public sealed class ResearchDbFactory : IDesignTimeDbContextFactory<ResearchDb>
{
    public ResearchDb CreateDbContext(string[] args) => new(ResearchDb.Options(
        Environment.GetEnvironmentVariable("KEIBUILD_RESEARCH_DB") ?? "Host=127.0.0.1;Database=keibuild;Username=keibuild"));
}
