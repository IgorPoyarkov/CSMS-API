using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Ревизия документа об изменении/расторжении — токен оптимистичной
    /// блокировки. Без неё проверка xmin в WHERE всегда совпадала: хранилище
    /// перечитывало строку и брало уже новое значение, поэтому две
    /// одновременные правки молча перезаписывали друг друга.
    ///
    /// Существующие строки получают ревизию 1 — это соответствует состоянию,
    /// в котором документ был создан.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261120140000_AddContractChangeRevision")]
    public partial class AddContractChangeRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Revision",
                schema: "contract",
                table: "contract_changes",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Revision",
                schema: "contract",
                table: "contract_changes");
        }
    }
}
