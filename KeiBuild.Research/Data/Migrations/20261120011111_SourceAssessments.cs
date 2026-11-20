using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeiBuild.Research.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessHold",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Assessment",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscoveryProvider",
                schema: "research",
                table: "Queries",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessHold",
                schema: "research",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "Assessment",
                schema: "research",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "DiscoveryProvider",
                schema: "research",
                table: "Queries");
        }
    }
}
