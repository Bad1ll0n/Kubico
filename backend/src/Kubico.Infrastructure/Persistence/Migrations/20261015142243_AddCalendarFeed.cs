using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CalendarFeedToken",
                table: "Memberships",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_CalendarFeedToken",
                table: "Memberships",
                column: "CalendarFeedToken",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Memberships_CalendarFeedToken",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "CalendarFeedToken",
                table: "Memberships");
        }
    }
}
