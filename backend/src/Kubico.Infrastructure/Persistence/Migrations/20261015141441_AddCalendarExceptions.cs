using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kubico.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<DateOnly>>(
                name: "ExcludedDates",
                table: "CalendarEvents",
                type: "date[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<DateOnly>(
                name: "OriginalDate",
                table: "CalendarEvents",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "CalendarEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarEvents_SeriesId_OriginalDate",
                table: "CalendarEvents",
                columns: new[] { "SeriesId", "OriginalDate" });

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarEvents_CalendarEvents_SeriesId",
                table: "CalendarEvents",
                column: "SeriesId",
                principalTable: "CalendarEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CalendarEvents_CalendarEvents_SeriesId",
                table: "CalendarEvents");

            migrationBuilder.DropIndex(
                name: "IX_CalendarEvents_SeriesId_OriginalDate",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "ExcludedDates",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "OriginalDate",
                table: "CalendarEvents");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "CalendarEvents");
        }
    }
}
