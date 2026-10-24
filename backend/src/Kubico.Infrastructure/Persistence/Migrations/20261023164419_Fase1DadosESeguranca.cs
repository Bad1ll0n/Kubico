using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fase1DadosESeguranca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "RewardRedemptions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReplacedAt",
                table: "RefreshTokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "PushDevices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Memberships",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "MaxUses",
                table: "Invites",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsedCount",
                table: "Invites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DueDay",
                table: "Chores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "Chores",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "BonusPerChoreCents",
                table: "AllowancePayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PrivateToUserId",
                table: "ActivityEntries",
                type: "uuid",
                nullable: true);

            // As alterações já registadas passam a pedir "gerir" para se desfazerem (o mais seguro: o histórico só
            // guarda 30 dias).
            migrationBuilder.AddColumn<string>(
                name: "RequiredAccess",
                table: "ActivityEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manage");

            // As tarefas que já existem ficam com o dia de referência do prazo atual (as mensais que já tinham descido
            // para 28 depois de fevereiro ficam a 28; muda-se o prazo para voltar ao dia certo).
            migrationBuilder.Sql("""UPDATE "Chores" SET "DueDay" = EXTRACT(DAY FROM "DueDate")::integer WHERE "DueDate" IS NOT NULL;""");

            migrationBuilder.CreateIndex(
                name: "IX_PushDevices_SessionId",
                table: "PushDevices",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PushDevices_SessionId",
                table: "PushDevices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RewardRedemptions");

            migrationBuilder.DropColumn(
                name: "ReplacedAt",
                table: "RefreshTokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "PushDevices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Memberships");

            migrationBuilder.DropColumn(
                name: "MaxUses",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "UsedCount",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "DueDay",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "BonusPerChoreCents",
                table: "AllowancePayments");

            migrationBuilder.DropColumn(
                name: "PrivateToUserId",
                table: "ActivityEntries");

            migrationBuilder.DropColumn(
                name: "RequiredAccess",
                table: "ActivityEntries");
        }
    }
}
