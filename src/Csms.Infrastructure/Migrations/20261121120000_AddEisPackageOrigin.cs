using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Б4: происхождение пакета ЕИС. Ссылка на документ-источник и снимок
    /// реквизитов заказчика хранятся jsonb — пакет живёт годами, а карточку
    /// организации правят, поэтому подпись нужно объяснять снимком «на ту дату».
    /// Колонки необязательные: старые пакеты и старый фронт продолжают работать.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261121120000_AddEisPackageOrigin")]
    public partial class AddEisPackageOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "eis",
                table: "packages",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Customer",
                schema: "eis",
                table: "packages",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                schema: "eis",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "Customer",
                schema: "eis",
                table: "packages");
        }
    }
}
