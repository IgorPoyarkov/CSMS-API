using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContractSmpRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contract_smp_records",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContractName = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContractPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RequiredPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    EisRegistryNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Okpd2Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PriceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Period = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SupplierName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SupplierInn = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    SupplierKpp = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    IsSmpSubject = table.Column<bool>(type: "boolean", nullable: false),
                    SubContractRecordId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsReplacementSubContract = table.Column<bool>(type: "boolean", nullable: false),
                    PaidEarlierThanContractSchedule = table.Column<bool>(type: "boolean", nullable: false),
                    PaidToSupplierDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AcceptanceSignedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SourceFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SupplierConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupplierConfirmedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SupplierConfirmedDocument = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CustomerConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CustomerConfirmedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CustomerConfirmedDocument = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PushedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PushedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EisPublishDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EisRecordNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_smp_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "contract_smp_record_documents",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_smp_record_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contract_smp_record_documents_contract_smp_records_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "contract",
                        principalTable: "contract_smp_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contract_smp_record_events",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_smp_record_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contract_smp_record_events_contract_smp_records_RecordId",
                        column: x => x.RecordId,
                        principalSchema: "contract",
                        principalTable: "contract_smp_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_record_documents_RecordId_OrderIndex",
                schema: "contract",
                table: "contract_smp_record_documents",
                columns: new[] { "RecordId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_record_events_RecordId_OrderIndex",
                schema: "contract",
                table: "contract_smp_record_events",
                columns: new[] { "RecordId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_records_ContractId",
                schema: "contract",
                table: "contract_smp_records",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_records_DocumentType",
                schema: "contract",
                table: "contract_smp_records",
                column: "DocumentType");

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_records_OrganizationId_ContractId",
                schema: "contract",
                table: "contract_smp_records",
                columns: new[] { "OrganizationId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_records_Status",
                schema: "contract",
                table: "contract_smp_records",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_contract_smp_records_SupplierInn",
                schema: "contract",
                table: "contract_smp_records",
                column: "SupplierInn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_smp_record_documents",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "contract_smp_record_events",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "contract_smp_records",
                schema: "contract");
        }
    }
}
