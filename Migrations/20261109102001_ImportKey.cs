using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyTracker.Migrations
{
    /// <inheritdoc />
    public partial class ImportKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assignments_OwnerId",
                table: "Assignments");

            migrationBuilder.AddColumn<string>(
                name: "ImportKey",
                table: "Assignments",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_OwnerId_ImportKey",
                table: "Assignments",
                columns: new[] { "OwnerId", "ImportKey" },
                unique: true,
                filter: "\"ImportKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Assignments_OwnerId_ImportKey",
                table: "Assignments");

            migrationBuilder.DropColumn(
                name: "ImportKey",
                table: "Assignments");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_OwnerId",
                table: "Assignments",
                column: "OwnerId");
        }
    }
}
