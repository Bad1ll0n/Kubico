using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemindersAndSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "MorningAt",
                table: "NotificationPreferences",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(7, 30, 0));

            migrationBuilder.AddColumn<bool>(
                name: "MorningSummary",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WeeklySummary",
                table: "NotificationPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoApproveRewardsUpTo",
                table: "Households",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "RemindAt",
                table: "Chores",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<List<int>>(
                name: "ReminderMinutes",
                table: "CalendarEvents",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "SentReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentReminders_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_SentAt",
                table: "SentReminders",
                column: "SentAt");

            migrationBuilder.CreateIndex(
                name: "IX_SentReminders_UserId_Kind_EntityId_DueAt",
                table: "SentReminders",
                columns: new[] { "UserId", "Kind", "EntityId", "DueAt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentReminders");

            migrationBuilder.DropColumn(
                name: "MorningAt",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "MorningSummary",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "WeeklySummary",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "AutoApproveRewardsUpTo",
                table: "Households");

            migrationBuilder.DropColumn(
                name: "RemindAt",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "ReminderMinutes",
                table: "CalendarEvents");
        }
    }
}
