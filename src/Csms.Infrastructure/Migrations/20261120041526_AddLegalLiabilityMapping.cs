using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Csms.Infrastructure.Migrations
{
    /// <summary>
    /// Схема сопоставления обязанностей и норм ответственности. DDL написан
    /// вручную: vector/tsvector не имеют CLR-типов в базовом Npgsql, поэтому
    /// индексы и generated-колонка создаются SQL, а код ходит через raw queries.
    /// </summary>
    public partial class AddLegalLiabilityMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS vector;
                CREATE SCHEMA IF NOT EXISTS legal;

                CREATE TABLE IF NOT EXISTS legal.liability_norms (
                    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    liability_node_id   text NOT NULL,
                    act_code            text NOT NULL,
                    act_version         text NOT NULL,
                    document_title      text,
                    structural_kind     text NOT NULL,
                    structural_path     jsonb NOT NULL DEFAULT '[]'::jsonb,
                    norm_reference      text NOT NULL,
                    heading             text,
                    text                text NOT NULL,
                    norm_type           text NOT NULL DEFAULT 'special'
                        CHECK (norm_type IN ('special','general','blanket','procedural','other')),
                    status              text NOT NULL DEFAULT 'in_force'
                        CHECK (status IN ('in_force','expired','amended','unknown')),
                    edition_date        date,
                    effective_from      date,
                    effective_to        date,
                    metadata            jsonb NOT NULL DEFAULT '{}'::jsonb,
                    source_url          text,
                    created_at          timestamptz NOT NULL DEFAULT now(),
                    updated_at          timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_liability_norm_node
                        UNIQUE (act_code, act_version, liability_node_id)
                );
                CREATE INDEX IF NOT EXISTS ix_liability_norm_act
                    ON legal.liability_norms (act_code, act_version, status);
                CREATE INDEX IF NOT EXISTS ix_liability_norm_type
                    ON legal.liability_norms (act_code, norm_type);
                CREATE INDEX IF NOT EXISTS ix_liability_norm_meta
                    ON legal.liability_norms USING gin (metadata);

                CREATE TABLE IF NOT EXISTS legal.liability_norm_chunks (
                    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    norm_id      uuid NOT NULL
                        REFERENCES legal.liability_norms(id) ON DELETE CASCADE,
                    chunk_index  integer NOT NULL,
                    chunk_text   text NOT NULL,
                    embedding    vector(768) NOT NULL,
                    search_tsv   tsvector GENERATED ALWAYS AS
                        (to_tsvector('russian', chunk_text)) STORED,
                    token_count  integer NOT NULL DEFAULT 0,
                    created_at   timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_liability_chunk UNIQUE (norm_id, chunk_index),
                    CONSTRAINT ck_liability_chunk_index CHECK (chunk_index >= 0)
                );
                CREATE INDEX IF NOT EXISTS ix_liability_chunk_hnsw
                    ON legal.liability_norm_chunks USING hnsw (embedding vector_cosine_ops);
                CREATE INDEX IF NOT EXISTS ix_liability_chunk_fts
                    ON legal.liability_norm_chunks USING gin (search_tsv);
                CREATE INDEX IF NOT EXISTS ix_liability_chunk_norm
                    ON legal.liability_norm_chunks (norm_id);

                CREATE TABLE IF NOT EXISTS legal.mapping_candidates (
                    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    run_id            uuid NOT NULL,
                    duty_id           text NOT NULL,
                    liability_node_id text NOT NULL,
                    source            text NOT NULL
                        CHECK (source IN ('vector','fulltext','umbrella','merged')),
                    search_score      double precision NOT NULL,
                    rank              integer NOT NULL,
                    chunk_id          uuid
                        REFERENCES legal.liability_norm_chunks(id) ON DELETE SET NULL,
                    payload           jsonb NOT NULL,
                    created_at        timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_mapping_candidate
                        UNIQUE (run_id, duty_id, liability_node_id)
                );
                CREATE INDEX IF NOT EXISTS ix_mapping_candidates_run
                    ON legal.mapping_candidates (run_id, duty_id);
                CREATE INDEX IF NOT EXISTS ix_mapping_candidates_duty
                    ON legal.mapping_candidates (duty_id, created_at DESC);

                CREATE TABLE IF NOT EXISTS legal.llm_audit_log (
                    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    run_id            uuid NOT NULL,
                    duty_id           text NOT NULL,
                    stage             text NOT NULL
                        CHECK (stage IN ('hypothesis','retrieval','arbiter','validation')),
                    model             text NOT NULL,
                    temperature       numeric(3,2) NOT NULL DEFAULT 0
                        CHECK (temperature = 0),
                    prompt_version    text NOT NULL,
                    attempt           integer NOT NULL DEFAULT 1,
                    request_json      jsonb NOT NULL,
                    response_json     jsonb,
                    error_text        text,
                    duration_ms       integer NOT NULL DEFAULT 0,
                    validation_result text NOT NULL
                        CHECK (validation_result IN
                            ('valid','invalid','repaired','manual_review','skipped')),
                    created_at        timestamptz NOT NULL DEFAULT now()
                );
                CREATE INDEX IF NOT EXISTS ix_llm_audit_duty
                    ON legal.llm_audit_log (duty_id, created_at DESC);
                CREATE INDEX IF NOT EXISTS ix_llm_audit_run
                    ON legal.llm_audit_log (run_id, stage, attempt);

                CREATE TABLE IF NOT EXISTS legal.duty_liability_mappings (
                    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    run_id                 uuid NOT NULL,
                    duty_id                text NOT NULL,
                    liability_node_id      text NOT NULL,
                    match_degree           text NOT NULL
                        CHECK (match_degree IN ('full','partial','blanket')),
                    probability_score      numeric(4,3) NOT NULL
                        CHECK (probability_score BETWEEN 0 AND 1),
                    qualifying_conditions  jsonb NOT NULL DEFAULT '[]'::jsonb,
                    sanction_summary       text NOT NULL,
                    legal_reasoning        text NOT NULL,
                    requires_human_review  boolean NOT NULL,
                    hypotheses             jsonb NOT NULL DEFAULT '[]'::jsonb,
                    model_version          text NOT NULL,
                    prompt_version         text NOT NULL,
                    audit_id               uuid
                        REFERENCES legal.llm_audit_log(id) ON DELETE SET NULL,
                    calculated_at          timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT uq_duty_liability
                        UNIQUE (duty_id, liability_node_id)
                );
                CREATE INDEX IF NOT EXISTS ix_duty_liability_run
                    ON legal.duty_liability_mappings (run_id);
                CREATE INDEX IF NOT EXISTS ix_duty_liability_review
                    ON legal.duty_liability_mappings
                       (requires_human_review, calculated_at DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS legal.duty_liability_mappings;
                DROP TABLE IF EXISTS legal.llm_audit_log;
                DROP TABLE IF EXISTS legal.mapping_candidates;
                DROP TABLE IF EXISTS legal.liability_norm_chunks;
                DROP TABLE IF EXISTS legal.liability_norms;
                DROP SCHEMA IF EXISTS legal;
                """);
        }
    }
}
