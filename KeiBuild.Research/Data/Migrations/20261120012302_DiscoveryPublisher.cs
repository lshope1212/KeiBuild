using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeiBuild.Research.Data.Migrations
{
    /// <inheritdoc />
    public partial class DiscoveryPublisher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Publisher",
                schema: "research",
                table: "Resources",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Publisher",
                schema: "research",
                table: "Resources");
        }
    }
}
