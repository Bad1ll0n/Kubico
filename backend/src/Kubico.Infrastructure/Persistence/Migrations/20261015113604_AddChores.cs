using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Households",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Europe/Lisbon");

            migrationBuilder.CreateTable(
                name: "Chores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Recurrence = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssigneeUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RotationUserIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chores", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chores_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChoreCompletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChoreCompletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChoreCompletions_Chores_ChoreId",
                        column: x => x.ChoreId,
                        principalTable: "Chores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreCompletions_ChoreId_CompletedAt",
                table: "ChoreCompletions",
                columns: new[] { "ChoreId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreCompletions_UserId",
                table: "ChoreCompletions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Chores_HouseholdId_DueDate",
                table: "Chores",
                columns: new[] { "HouseholdId", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChoreCompletions");

            migrationBuilder.DropTable(
                name: "Chores");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "Households");
        }
    }
}
