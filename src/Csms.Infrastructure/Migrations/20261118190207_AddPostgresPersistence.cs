using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgresPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "eis");

            migrationBuilder.CreateTable(
                name: "package_events",
                schema: "eis",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackageId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    From = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Comment = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "packages",
                schema: "eis",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractEisNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Ikz = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadXml = table.Column<string>(type: "text", nullable: false),
                    PayloadSchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignatureBase64 = table.Column<string>(type: "text", nullable: true),
                    SignerThumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EisGuid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    FlkErrors = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_packages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_package_events_PackageId_OrderIndex",
                schema: "eis",
                table: "package_events",
                columns: new[] { "PackageId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_packages_ContractEisNumber",
                schema: "eis",
                table: "packages",
                column: "ContractEisNumber");

            migrationBuilder.CreateIndex(
                name: "IX_packages_Status",
                schema: "eis",
                table: "packages",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "package_events",
                schema: "eis");

            migrationBuilder.DropTable(
                name: "packages",
                schema: "eis");
        }
    }
}
