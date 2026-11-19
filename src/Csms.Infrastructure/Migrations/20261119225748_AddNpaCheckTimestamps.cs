using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNpaCheckTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastCheckMessage",
                schema: "ref",
                table: "npa_documents",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckStatus",
                schema: "ref",
                table: "npa_documents",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCheckedAt",
                schema: "ref",
                table: "npa_documents",
                type: "timestamp with time zone",
                nullable: true);

            // У уже опубликованных версий время проверки совпадает с временем
            // публикации: отдельной отметки раньше не хранили.
            migrationBuilder.Sql("""
                UPDATE ref.npa_documents
                SET "LastCheckedAt" = "FetchedAt",
                    "LastCheckStatus" = 'published',
                    "LastCheckMessage" = 'Время проверки восстановлено из времени публикации версии.'
                WHERE "LastCheckedAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckMessage",
                schema: "ref",
                table: "npa_documents");

            migrationBuilder.DropColumn(
                name: "LastCheckStatus",
                schema: "ref",
                table: "npa_documents");

            migrationBuilder.DropColumn(
                name: "LastCheckedAt",
                schema: "ref",
                table: "npa_documents");
        }
    }
}
