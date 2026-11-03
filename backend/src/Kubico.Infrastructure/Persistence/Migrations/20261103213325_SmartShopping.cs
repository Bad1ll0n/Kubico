using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmartShopping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PurchaseRecords",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseRecords", x => x.ItemId);
                    table.ForeignKey(
                        name: "FK_PurchaseRecords_ShoppingLists_ListId",
                        column: x => x.ListId,
                        principalTable: "ShoppingLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurringItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IntervalDays = table.Column<int>(type: "integer", nullable: false),
                    NextDate = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringItems_ShoppingLists_ListId",
                        column: x => x.ListId,
                        principalTable: "ShoppingLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRecords_ListId_PurchasedAt",
                table: "PurchaseRecords",
                columns: new[] { "ListId", "PurchasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRecords_PurchasedAt",
                table: "PurchaseRecords",
                column: "PurchasedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseRecords_UserId",
                table: "PurchaseRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_ListId",
                table: "RecurringItems",
                column: "ListId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringItems_NextDate",
                table: "RecurringItems",
                column: "NextDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseRecords");

            migrationBuilder.DropTable(
                name: "RecurringItems");
        }
    }
}
