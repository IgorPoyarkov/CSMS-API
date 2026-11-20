using System;
using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Электронное актирование: документы о приёмке (ст. 94 44-ФЗ), их позиции,
    /// выявленные недостатки и журнал переходов.
    ///
    /// Схема та же, что у изменений контракта: это документы исполнения, а не
    /// пакеты обмена с ЕИС. Снимок контракта и условий приёмки хранится в
    /// строке документа, чтобы приёмку нельзя было пересчитать по изменённой
    /// карточке контракта.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261120160000_AddContractAcceptance")]
    public partial class AddContractAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contract_acceptances",
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
                    SupplierInn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Ikz = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EisRegistryNumber = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    StageNumber = table.Column<int>(type: "integer", nullable: true),
                    StageName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CorrectedDocumentNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CorrectedDocumentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CorrectionReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AcceptanceTermDays = table.Column<int>(type: "integer", nullable: false),
                    ExpertiseRequired = table.Column<bool>(type: "boolean", nullable: false),
                    ExpertiseByThirdParty = table.Column<bool>(type: "boolean", nullable: false),
                    ExpertiseOrganization = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ExpertiseConclusionNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpertiseConclusionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CommissionRequired = table.Column<bool>(type: "boolean", nullable: false),
                    CommissionDecisionNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CommissionDecisionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DeliveryPlace = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegisteredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EisRegistrationNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EisRegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RefusalReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReturnComment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CancelledReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_acceptances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "acceptance_positions",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Okpd2 = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Unit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    CountryOrigin = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RegistryNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acceptance_positions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_acceptance_positions_contract_acceptances_AcceptanceId",
                        column: x => x.AcceptanceId,
                        principalSchema: "contract",
                        principalTable: "contract_acceptances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "acceptance_defects",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Requirement = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_acceptance_defects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_acceptance_defects_contract_acceptances_AcceptanceId",
                        column: x => x.AcceptanceId,
                        principalSchema: "contract",
                        principalTable: "contract_acceptances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "acceptance_events",
                schema: "contract",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptanceId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_acceptance_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_acceptance_events_contract_acceptances_AcceptanceId",
                        column: x => x.AcceptanceId,
                        principalSchema: "contract",
                        principalTable: "contract_acceptances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_acceptances_ContractId",
                schema: "contract",
                table: "contract_acceptances",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_contract_acceptances_OrganizationId_ContractId",
                schema: "contract",
                table: "contract_acceptances",
                columns: new[] { "OrganizationId", "ContractId" });

            migrationBuilder.CreateIndex(
                name: "IX_contract_acceptances_Status",
                schema: "contract",
                table: "contract_acceptances",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_positions_AcceptanceId_OrderIndex",
                schema: "contract",
                table: "acceptance_positions",
                columns: new[] { "AcceptanceId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_defects_AcceptanceId_OrderIndex",
                schema: "contract",
                table: "acceptance_defects",
                columns: new[] { "AcceptanceId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_acceptance_events_AcceptanceId_OrderIndex",
                schema: "contract",
                table: "acceptance_events",
                columns: new[] { "AcceptanceId", "OrderIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acceptance_defects",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "acceptance_events",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "acceptance_positions",
                schema: "contract");

            migrationBuilder.DropTable(
                name: "contract_acceptances",
                schema: "contract");
        }
    }
}
