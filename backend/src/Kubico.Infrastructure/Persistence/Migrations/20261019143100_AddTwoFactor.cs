using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedTwoFactorAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LastTotpStep",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingTotpSecret",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "RecoveryCodeHashes",
                table: "Users",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "TotpSecret",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TwoFactorLockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedTwoFactorAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastTotpStep",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PendingTotpSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RecoveryCodeHashes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TotpSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorLockedUntil",
                table: "Users");
        }
    }
}
