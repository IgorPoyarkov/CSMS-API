using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceDatasets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ref");

            migrationBuilder.CreateTable(
                name: "datasets",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceDocument = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SourceSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_datasets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "items",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DatasetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParentCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Actual = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_items_datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalSchema: "ref",
                        principalTable: "datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_datasets_Code",
                schema: "ref",
                table: "datasets",
                column: "Code",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_datasets_Code_Version",
                schema: "ref",
                table: "datasets",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_items_DatasetId_Code",
                schema: "ref",
                table: "items",
                columns: new[] { "DatasetId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_items_DatasetId_ParentCode",
                schema: "ref",
                table: "items",
                columns: new[] { "DatasetId", "ParentCode" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "items",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "datasets",
                schema: "ref");
        }
    }
}
