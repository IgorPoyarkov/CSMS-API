using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContractChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "contract");

            migrationBuilder.CreateTable(
                name: "contract_changes",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContractNumber = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ContractName = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ContractPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ContractStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContractDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SupplierName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Ikz = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EisRegistryNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ContractQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ContractExecutionEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BasisCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ChangeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TerminationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NewPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    NewQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    QuantityUnit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NewSubject = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NewSupplier = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    NewExecutionEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NoticeNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NoticeDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CourtDecisionNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CourtDecisionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    GovernmentActNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GovernmentActDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FormulaText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    InitialMaxPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegisteredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EisRegistrationNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EisRegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RejectedReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CancelledReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_changes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "contract_change_documents",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_contract_change_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contract_change_documents_contract_changes_ChangeId",
                        column: x => x.ChangeId,
                        principalSchema: "contract",
                        principalTable: "contract_changes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contract_change_events",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    To = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_change_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contract_change_events_contract_changes_ChangeId",
                        column: x => x.ChangeId,
                        principalSchema: "contract",
                        principalTable: "contract_changes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_change_documents_ChangeId_OrderIndex",
                schema: "contract",
                table: "contract_change_documents",
                columns: new[] { "ChangeId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_change_events_ChangeId_OrderIndex",
                schema: "contract",
                table: "contract_change_events",
                columns: new[] { "ChangeId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_changes_ContractId",
                schema: "contract",
                table: "contract_changes",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_contract_changes_OrganizationId_ContractId",
                schema: "contract",
                table: "contract_changes",
                columns: new[] { "OrganizationId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_changes_Status",
                schema: "contract",
                table: "contract_changes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_change_documents",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "contract_change_events",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "contract_changes",
                schema: "contract");
        }
    }
}
