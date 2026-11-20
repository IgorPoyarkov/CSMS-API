using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// В первой миграции колонка norm_reference в таблице связей отсутствовала,
    /// хотя GetResultAsync её читает. Добавляем колонку и подтягиваем значение
    /// из справочника норм для уже сохранённых связей.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261120120000_AddMappingNormReference")]
    public partial class AddMappingNormReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE legal.duty_liability_mappings
                    ADD COLUMN IF NOT EXISTS norm_reference text NOT NULL DEFAULT '';

                UPDATE legal.duty_liability_mappings m
                   SET norm_reference = COALESCE(n.norm_reference, '')
                  FROM legal.liability_norms n
                 WHERE n.liability_node_id = m.liability_node_id
                   AND m.norm_reference = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE legal.duty_liability_mappings DROP COLUMN IF EXISTS norm_reference;");
        }
    }
}
