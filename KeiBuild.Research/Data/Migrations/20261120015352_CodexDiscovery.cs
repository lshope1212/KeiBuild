using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeiBuild.Research.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodexDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CitationsJson",
                schema: "research",
                table: "SearchAttempts",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiscoveryInputTokens",
                schema: "research",
                table: "Runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DiscoveryOutputTokens",
                schema: "research",
                table: "Runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryProvider",
                schema: "research",
                table: "Runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryCategory",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryObservation",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryReason",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CitationsJson",
                schema: "research",
                table: "SearchAttempts");

            migrationBuilder.DropColumn(
                name: "DiscoveryInputTokens",
                schema: "research",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DiscoveryOutputTokens",
                schema: "research",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DiscoveryProvider",
                schema: "research",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DiscoveryCategory",
                schema: "research",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "DiscoveryObservation",
                schema: "research",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "DiscoveryReason",
                schema: "research",
                table: "Resources");
        }
    }
}
