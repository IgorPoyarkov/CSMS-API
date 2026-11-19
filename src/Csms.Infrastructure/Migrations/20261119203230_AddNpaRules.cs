using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNpaRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "npa_documents",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EoNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PublishDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PagesCount = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    PdfSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OcrEngine = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    UnresolvedJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_npa_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "npa_rules",
                schema: "ref",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Okpd2Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConditionText = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Quote = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    IsInherited = table.Column<bool>(type: "boolean", nullable: false),
                    InheritedFrom = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SourcePage = table.Column<int>(type: "integer", nullable: true),
                    SourceRow = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_npa_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_npa_rules_npa_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "ref",
                        principalTable: "npa_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_npa_documents_Code",
                schema: "ref",
                table: "npa_documents",
                column: "Code",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_npa_documents_Code_Version",
                schema: "ref",
                table: "npa_documents",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_npa_rules_document_code",
                schema: "ref",
                table: "npa_rules",
                columns: new[] { "DocumentId", "Okpd2Code" });

            migrationBuilder.CreateIndex(
                name: "IX_npa_rules_DocumentId_Okpd2Code_IsInherited",
                schema: "ref",
                table: "npa_rules",
                columns: new[] { "DocumentId", "Okpd2Code", "IsInherited" },
                unique: true);

            // Префиксный индекс для протяжки правил по дереву ОКПД2
            // (LIKE '26.20.%'): обычный btree с C-колляцией не ускоряет LIKE.
            migrationBuilder.Sql(
                "CREATE INDEX ix_npa_rules_code_prefix ON ref.npa_rules " +
                "(\"Okpd2Code\" text_pattern_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ref.ix_npa_rules_code_prefix;");

            migrationBuilder.DropTable(
                name: "npa_rules",
                schema: "ref");

            migrationBuilder.DropTable(
                name: "npa_documents",
                schema: "ref");
        }
    }
}
