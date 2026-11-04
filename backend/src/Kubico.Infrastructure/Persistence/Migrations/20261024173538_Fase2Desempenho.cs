using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fase2Desempenho : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_PushedAt",
                table: "Notifications");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextPushAt",
                table: "NotificationPreferences",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "RecurrenceEndDate",
                table: "CalendarEvents",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingItems_AddedByUserId",
                table: "ShoppingItems",
                column: "AddedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RewardRedemptions_UserId",
                table: "RewardRedemptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                table: "RefreshTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Pending",
                table: "Notifications",
                column: "UserId",
                filter: "\"PushedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_CreatedByUserId",
                table: "CalendarEvents",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Allowances_UserId",
                table: "Allowances",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AllowancePayments_UserId",
                table: "AllowancePayments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_CreatedAt",
                table: "ActivityEntries",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShoppingItems_AddedByUserId",
                table: "ShoppingItems");

            migrationBuilder.DropIndex(
                name: "IX_RewardRedemptions_UserId",
                table: "RewardRedemptions");

            migrationBuilder.DropIndex(
                name: "IX_RefreshTokens_ExpiresAt",
                table: "RefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_Pending",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_CreatedByUserId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_Allowances_UserId",
                table: "Allowances");

            migrationBuilder.DropIndex(
                name: "IX_AllowancePayments_UserId",
                table: "AllowancePayments");

            migrationBuilder.DropIndex(
                name: "IX_ActivityEntries_CreatedAt",
                table: "ActivityEntries");

            migrationBuilder.DropColumn(
                name: "NextPushAt",
                table: "NotificationPreferences");

            migrationBuilder.DropColumn(
                name: "RecurrenceEndDate",
                table: "CalendarEvents");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_PushedAt",
                table: "Notifications",
                column: "PushedAt");
        }
    }
}
