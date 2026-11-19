using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeiBuild.Research.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialResearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.EnsureSchema(
                name: "research");

            migrationBuilder.CreateTable(
                name: "Parts",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Manufacturer = table.Column<string>(type: "text", nullable: false),
                    ManufacturerKey = table.Column<string>(type: "text", nullable: false),
                    PartNumberKey = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Parts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VehicleModels",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Make = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PartIdentifiers",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartIdentifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartIdentifiers_Parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "catalog",
                        principalTable: "Parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleVariants",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Chassis = table.Column<string>(type: "text", nullable: false),
                    Engine = table.Column<string>(type: "text", nullable: false),
                    Era = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleVariants_VehicleModels_VehicleModelId",
                        column: x => x.VehicleModelId,
                        principalSchema: "catalog",
                        principalTable: "VehicleModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Campaigns",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    VehicleVariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Campaigns_VehicleVariants_VehicleVariantId",
                        column: x => x.VehicleVariantId,
                        principalSchema: "catalog",
                        principalTable: "VehicleVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimKey = table.Column<string>(type: "text", nullable: false),
                    GroupKey = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    Attribute = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    Manufacturer = table.Column<string>(type: "text", nullable: true),
                    PartNumber = table.Column<string>(type: "text", nullable: true),
                    PartNumberKey = table.Column<string>(type: "text", nullable: true),
                    RelatedPartNumber = table.Column<string>(type: "text", nullable: true),
                    Applicability = table.Column<string>(type: "text", nullable: true),
                    Conflict = table.Column<bool>(type: "boolean", nullable: false),
                    PossibleMatch = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Claims_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Contents",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    EncodingKey = table.Column<string>(type: "text", nullable: false),
                    AnalysisJson = table.Column<string>(type: "jsonb", nullable: true),
                    AnalyzedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Contents_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Prefix = table.Column<bool>(type: "boolean", nullable: false),
                    Publisher = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    PermissionNotes = table.Column<string>(type: "text", nullable: false),
                    AllowFetch = table.Column<bool>(type: "boolean", nullable: false),
                    AllowAi = table.Column<bool>(type: "boolean", nullable: false),
                    AllowEvidence = table.Column<bool>(type: "boolean", nullable: false),
                    AllowSnapshot = table.Column<bool>(type: "boolean", nullable: false),
                    ExploreLinks = table.Column<bool>(type: "boolean", nullable: false),
                    SitemapUrl = table.Column<string>(type: "text", nullable: true),
                    DelaySeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Policies_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Searches = table.Column<int>(type: "integer", nullable: false),
                    Pages = table.Column<int>(type: "integer", nullable: false),
                    AiCalls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedUsd = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: false),
                    DuplicateUrls = table.Column<int>(type: "integer", nullable: false),
                    DuplicateDocuments = table.Column<int>(type: "integer", nullable: false),
                    DuplicateClaims = table.Column<int>(type: "integer", nullable: false),
                    WithheldClaims = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Topics",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Topics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Topics_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Decisions",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Decisions_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "research",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Queries",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    TopicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Queries_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Queries_Topics_TopicId",
                        column: x => x.TopicId,
                        principalSchema: "research",
                        principalTable: "Topics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovedKnowledge",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehicleVariantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PartId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovedKnowledge", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApprovedKnowledge_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "research",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovedKnowledge_Decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalSchema: "research",
                        principalTable: "Decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovedKnowledge_Parts_PartId",
                        column: x => x.PartId,
                        principalSchema: "catalog",
                        principalTable: "Parts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApprovedKnowledge_VehicleVariants_VehicleVariantId",
                        column: x => x.VehicleVariantId,
                        principalSchema: "catalog",
                        principalTable: "VehicleVariants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    SearchQueryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Snippet = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Relevance = table.Column<int>(type: "integer", nullable: false),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Resources_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "research",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Resources_Queries_SearchQueryId",
                        column: x => x.SearchQueryId,
                        principalSchema: "research",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Resources_Resources_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "research",
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SearchAttempts",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    QueryId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Returned = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SearchAttempts_Queries_QueryId",
                        column: x => x.QueryId,
                        principalSchema: "research",
                        principalTable: "Queries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SearchAttempts_Runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "research",
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalSchema: "research",
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Versions",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Publisher = table.Column<string>(type: "text", nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    RetrievedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<int>(type: "integer", nullable: false),
                    Snapshot = table.Column<byte[]>(type: "bytea", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Versions_Contents_ContentId",
                        column: x => x.ContentId,
                        principalSchema: "research",
                        principalTable: "Contents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Versions_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "research",
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Versions_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalSchema: "research",
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CrawlAttempts",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AiReserved = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrawlAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrawlAttempts_Jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "research",
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrawlAttempts_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "research",
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrawlAttempts_Runs_RunId",
                        column: x => x.RunId,
                        principalSchema: "research",
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CrawlAttempts_Versions_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "research",
                        principalTable: "Versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evidence",
                schema: "research",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Locator = table.Column<string>(type: "text", nullable: false),
                    Passage = table.Column<string>(type: "text", nullable: false),
                    EnglishTranslation = table.Column<string>(type: "text", nullable: true),
                    FitmentJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Evidence_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "research",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidence_Versions_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "research",
                        principalTable: "Versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedKnowledge_ClaimId",
                schema: "catalog",
                table: "ApprovedKnowledge",
                column: "ClaimId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedKnowledge_DecisionId",
                schema: "catalog",
                table: "ApprovedKnowledge",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedKnowledge_PartId",
                schema: "catalog",
                table: "ApprovedKnowledge",
                column: "PartId");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovedKnowledge_VehicleVariantId",
                schema: "catalog",
                table: "ApprovedKnowledge",
                column: "VehicleVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Name",
                schema: "research",
                table: "Campaigns",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_VehicleVariantId",
                schema: "research",
                table: "Campaigns",
                column: "VehicleVariantId");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_CampaignId_ClaimKey",
                schema: "research",
                table: "Claims",
                columns: new[] { "CampaignId", "ClaimKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claims_CampaignId_GroupKey",
                schema: "research",
                table: "Claims",
                columns: new[] { "CampaignId", "GroupKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_CampaignId_PartNumberKey",
                schema: "research",
                table: "Claims",
                columns: new[] { "CampaignId", "PartNumberKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Contents_CampaignId_Sha256_Format_EncodingKey",
                schema: "research",
                table: "Contents",
                columns: new[] { "CampaignId", "Sha256", "Format", "EncodingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CrawlAttempts_JobId",
                schema: "research",
                table: "CrawlAttempts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_CrawlAttempts_PolicyId",
                schema: "research",
                table: "CrawlAttempts",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_CrawlAttempts_RunId",
                schema: "research",
                table: "CrawlAttempts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_CrawlAttempts_VersionId",
                schema: "research",
                table: "CrawlAttempts",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Decisions_ClaimId",
                schema: "research",
                table: "Decisions",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_ClaimId_VersionId_Locator",
                schema: "research",
                table: "Evidence",
                columns: new[] { "ClaimId", "VersionId", "Locator" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_VersionId",
                schema: "research",
                table: "Evidence",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ResourceId",
                schema: "research",
                table: "Jobs",
                column: "ResourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_State_DueAt",
                schema: "research",
                table: "Jobs",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PartIdentifiers_PartId_Number_Kind",
                schema: "catalog",
                table: "PartIdentifiers",
                columns: new[] { "PartId", "Number", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Parts_ManufacturerKey_PartNumberKey",
                schema: "catalog",
                table: "Parts",
                columns: new[] { "ManufacturerKey", "PartNumberKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_CampaignId_Key",
                schema: "research",
                table: "Policies",
                columns: new[] { "CampaignId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Queries_CampaignId_Language_Text",
                schema: "research",
                table: "Queries",
                columns: new[] { "CampaignId", "Language", "Text" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Queries_CampaignId_State_DueAt",
                schema: "research",
                table: "Queries",
                columns: new[] { "CampaignId", "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Queries_TopicId",
                schema: "research",
                table: "Queries",
                column: "TopicId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_CampaignId_Host_Relevance",
                schema: "research",
                table: "Resources",
                columns: new[] { "CampaignId", "Host", "Relevance" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_CampaignId_Url",
                schema: "research",
                table: "Resources",
                columns: new[] { "CampaignId", "Url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ParentId",
                schema: "research",
                table: "Resources",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Resources_SearchQueryId",
                schema: "research",
                table: "Resources",
                column: "SearchQueryId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_CampaignId",
                schema: "research",
                table: "Runs",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchAttempts_QueryId",
                schema: "research",
                table: "SearchAttempts",
                column: "QueryId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchAttempts_RunId",
                schema: "research",
                table: "SearchAttempts",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Topics_CampaignId_Key",
                schema: "research",
                table: "Topics",
                columns: new[] { "CampaignId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleModels_Make_Model",
                schema: "catalog",
                table: "VehicleModels",
                columns: new[] { "Make", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleVariants_VehicleModelId_Chassis_Engine",
                schema: "catalog",
                table: "VehicleVariants",
                columns: new[] { "VehicleModelId", "Chassis", "Engine" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Versions_ContentId",
                schema: "research",
                table: "Versions",
                column: "ContentId");

            migrationBuilder.CreateIndex(
                name: "IX_Versions_PolicyId",
                schema: "research",
                table: "Versions",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Versions_ResourceId_ContentId",
                schema: "research",
                table: "Versions",
                columns: new[] { "ResourceId", "ContentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovedKnowledge",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "CrawlAttempts",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Evidence",
                schema: "research");

            migrationBuilder.DropTable(
                name: "PartIdentifiers",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "SearchAttempts",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Decisions",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Jobs",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Versions",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Parts",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "Runs",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Claims",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Contents",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Policies",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Resources",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Queries",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Topics",
                schema: "research");

            migrationBuilder.DropTable(
                name: "Campaigns",
                schema: "research");

            migrationBuilder.DropTable(
                name: "VehicleVariants",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "VehicleModels",
                schema: "catalog");
        }
    }
}
