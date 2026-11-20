using Csms.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Каталог приложения N 2 к ПП РФ N 2604 и связей ОКПД2 с позициями.
    /// DDL написан вручную: импортёр и API ходят в эти таблицы raw SQL, как
    /// и в остальные таблицы схемы legal, чтобы не тащить их в EF-модель.
    /// </summary>
    [DbContext(typeof(EisDbContext))]
    [Migration("20261120150000_AddPp2604Catalog")]
    public partial class AddPp2604Catalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS legal.pp2604_documents (
                    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    version             text NOT NULL UNIQUE,
                    rules_version       text NOT NULL,
                    eo_number           text NOT NULL,
                    api_url             text NOT NULL,
                    pdf_url             text NOT NULL,
                    pdf_sha256          text NOT NULL,
                    document_title      text NOT NULL,
                    document_date       date,
                    publish_date        date,
                    pages_count         integer,
                    appendix_pages      text NOT NULL,
                    extraction_method   text NOT NULL,
                    source_act          text NOT NULL,
                    source_appendix     text NOT NULL,
                    source_portal       text NOT NULL,
                    source_classifier   text NOT NULL,
                    row_count           integer NOT NULL DEFAULT 0,
                    link_count          integer NOT NULL DEFAULT 0,
                    is_active           boolean NOT NULL DEFAULT false,
                    imported_at         timestamptz NOT NULL DEFAULT now()
                );

                CREATE TABLE IF NOT EXISTS legal.pp2604_categories (
                    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    document_version text NOT NULL
                        REFERENCES legal.pp2604_documents(version) ON DELETE CASCADE,
                    row_no          integer NOT NULL CHECK (row_no BETWEEN 1 AND 20),
                    category_id     text NOT NULL,
                    label           text NOT NULL,
                    appendix_name   text NOT NULL,
                    min_price       integer NOT NULL CHECK (min_price BETWEEN 0 AND 100),
                    max_expenses    integer CHECK (max_expenses BETWEEN 0 AND 100),
                    max_non_cost    integer NOT NULL CHECK (max_non_cost BETWEEN 0 AND 100),
                    source_page     integer NOT NULL,
                    created_at      timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_pp2604_category UNIQUE (document_version, row_no)
                );
                CREATE INDEX IF NOT EXISTS ix_pp2604_category_doc
                    ON legal.pp2604_categories (document_version);

                CREATE TABLE IF NOT EXISTS legal.pp2604_okpd2_links (
                    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    document_version text NOT NULL
                        REFERENCES legal.pp2604_documents(version) ON DELETE CASCADE,
                    okpd2_prefix    text NOT NULL,
                    category_id     text NOT NULL,
                    row_no          integer NOT NULL,
                    basis           text NOT NULL
                        CHECK (basis IN ('classifier','purpose','residual')),
                    classifier_name text NOT NULL,
                    condition_text  text,
                    needs_review    boolean NOT NULL DEFAULT false,
                    purpose_key     text,
                    source_text     text NOT NULL,
                    sort_order      integer NOT NULL DEFAULT 0,
                    created_at      timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_pp2604_link
                        UNIQUE (document_version, okpd2_prefix, category_id, basis, purpose_key)
                );
                CREATE INDEX IF NOT EXISTS ix_pp2604_link_doc
                    ON legal.pp2604_okpd2_links (document_version, sort_order);
                CREATE INDEX IF NOT EXISTS ix_pp2604_link_prefix
                    ON legal.pp2604_okpd2_links (document_version, okpd2_prefix);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS legal.pp2604_okpd2_links;
                DROP TABLE IF EXISTS legal.pp2604_categories;
                DROP TABLE IF EXISTS legal.pp2604_documents;
                """);
        }
    }
}
