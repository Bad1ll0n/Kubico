using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChoreRoutines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<int>>(
                name: "DoneSteps",
                table: "Chores",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'::integer[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "Steps",
                table: "Chores",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<string>(
                name: "TimeOfDay",
                table: "Chores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Any");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DoneSteps",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "Steps",
                table: "Chores");

            migrationBuilder.DropColumn(
                name: "TimeOfDay",
                table: "Chores");
        }
    }
}
