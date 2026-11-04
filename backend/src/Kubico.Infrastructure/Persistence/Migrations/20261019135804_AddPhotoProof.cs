using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotoProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RequiresPhoto",
                table: "Chores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "PhotoId",
                table: "ChoreCompletions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RejectedAt",
                table: "ChoreCompletions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RejectedByUserId",
                table: "ChoreCompletions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChorePhotos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChorePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChorePhotos_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChoreCompletions_PhotoId",
                table: "ChoreCompletions",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_ChorePhotos_HouseholdId",
                table: "ChorePhotos",
                column: "HouseholdId");

            migrationBuilder.AddForeignKey(
                name: "FK_ChoreCompletions_ChorePhotos_PhotoId",
                table: "ChoreCompletions",
                column: "PhotoId",
                principalTable: "ChorePhotos",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChoreCompletions_ChorePhotos_PhotoId",
                table: "ChoreCompletions");

            migrationBuilder.DropTable(
                name: "ChorePhotos");

            migrationBuilder.DropIndex(
                name: "IX_ChoreCompletions_PhotoId",
                table: "ChoreCompletions");

            migrationBuilder.DropColumn(
                name: "RequiresPhoto",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "PhotoId",
                table: "ChoreCompletions");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "ChoreCompletions");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "ChoreCompletions");
        }
    }
}
