using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Assistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssistantEnabled",
                table: "Households",
                type: "boolean",
                nullable: false,
                // As casas que já existem ficam com o assistente ligado, como as novas.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AssistantUsages",
                columns: table => new
                {
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantUsages", x => new { x.HouseholdId, x.Day });
                    table.ForeignKey(
                        name: "FK_AssistantUsages_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantUsages");

            migrationBuilder.DropColumn(
                name: "AssistantEnabled",
                table: "Households");
        }
    }
}
