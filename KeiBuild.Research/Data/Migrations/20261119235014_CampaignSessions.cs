using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeiBuild.Research.Data.Migrations
{
    /// <inheritdoc />
    public partial class CampaignSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "research",
                table: "Runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                schema: "research",
                table: "Runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GapPartNumber",
                schema: "research",
                table: "Queries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GapReason",
                schema: "research",
                table: "Queries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginClaimId",
                schema: "research",
                table: "Queries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExtractionAfter",
                schema: "research",
                table: "Campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SearchAfter",
                schema: "research",
                table: "Campaigns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StopRequested",
                schema: "research",
                table: "Campaigns",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Queries_OriginClaimId",
                schema: "research",
                table: "Queries",
                column: "OriginClaimId");

            migrationBuilder.AddForeignKey(
                name: "FK_Queries_Claims_OriginClaimId",
                schema: "research",
                table: "Queries",
                column: "OriginClaimId",
                principalSchema: "research",
                principalTable: "Claims",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Queries_Claims_OriginClaimId",
                schema: "research",
                table: "Queries");

            migrationBuilder.DropIndex(
                name: "IX_Queries_OriginClaimId",
                schema: "research",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "research",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "StopReason",
                schema: "research",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "GapPartNumber",
                schema: "research",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "GapReason",
                schema: "research",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "OriginClaimId",
                schema: "research",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "ExtractionAfter",
                schema: "research",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "SearchAfter",
                schema: "research",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "StopRequested",
                schema: "research",
                table: "Campaigns");
        }
    }
}
