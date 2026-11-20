using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Маркер завершённого прогона: пустой результат сопоставления не оставляет
    /// связей, поэтому идемпотентность фиксируется отдельным статусом кандидатов.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261120130000_AddMappingRunStatus")]
    public partial class AddMappingRunStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE legal.mapping_candidates
                    ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'pending';

                UPDATE legal.mapping_candidates c
                   SET status = 'completed'
                  FROM (
                      SELECT DISTINCT run_id FROM legal.duty_liability_mappings
                  ) m
                 WHERE c.run_id = m.run_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE legal.mapping_candidates DROP COLUMN IF EXISTS status;");
        }
    }
}
