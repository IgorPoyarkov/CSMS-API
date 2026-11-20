# Compliance & Liability Mapping — семантическое сопоставление обязанности и норм ответственности

Статус: **реализовано в `csms-api`** (2026-11-20). Контракты — в `Csms.Application`,
реализация — в `Csms.Infrastructure/Liability`, API — `LiabilityMappingEndpoints`.
Живой прогон требует PostgreSQL с установленным pgvector (см. §9).

## 1. Намерение и границы

**Сущность А** — структурированная обязанность: формулировка должного действия, объект, срок, условие, субъект, идентификаторы.
**Сущность Б** — нормативный акт, уже разобранный на статьи/части/пункты/подпункты с заголовками глав, реквизитами, текстом, статусом, датой редакции и метаданными.

Модуль строит **одну ко многим** связь «обязанность → нормы ответственности». Ключевые ограничения:

1. Арбитр видит **только** заранее собранный пул кандидатов. Норма вне пула не может появиться в результате — валидатор отбрасывает такие id и пишет аудит.
2. Общие и бланкетные нормы response включаются в пул принудительно (`umbrella boost`) и не теряются при дедупликации.
3. `match_degree` описывает **факт совпадения состава** нормы с гипотезой нарушения. Условия наступления ответственности (ущерб, последствия, вина, дополнительный факт) уходят только в `qualifying_conditions`. Это разные поля и разные вопросы агенту.
4. `irrelevant` не попадает в итоговый вывод, но остаётся в аудите.
5. Всё спорное уходит в `requires_human_review`: низкая вероятность, частичное/бланкетное совпадение, конфликт норм, неполные метаданные, ошибка валидации LLM.

## 2. Схема PostgreSQL

Схема `legal` (при необходимости меняется одна константа в `MappingSchema`). Требуется расширение `vector` (pgvector).

```sql
CREATE SCHEMA IF NOT EXISTS legal;
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE legal.liability_norms (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    liability_node_id   text NOT NULL,                    -- id структурной единицы Сущности Б
    act_code            text NOT NULL,
    act_version         text NOT NULL,
    document_title      text,
    structural_kind     text NOT NULL,                    -- chapter/article/part/clause/subclause
    structural_path     jsonb NOT NULL DEFAULT '[]'::jsonb, -- заголовки глав + хлебные крошки
    norm_reference      text NOT NULL,                    -- «ст. 15 ч. 1 п. 3»
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
    CONSTRAINT uq_liability_norm_node UNIQUE (act_code, act_version, liability_node_id)
);
CREATE INDEX ix_liability_norm_act ON legal.liability_norms (act_code, act_version, status);
CREATE INDEX ix_liability_norm_type ON legal.liability_norms (act_code, norm_type);
CREATE INDEX ix_liability_norm_meta ON legal.liability_norms USING gin (metadata);

CREATE TABLE legal.liability_norm_chunks (
    id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    norm_id      uuid NOT NULL REFERENCES legal.liability_norms(id) ON DELETE CASCADE,
    chunk_index  integer NOT NULL,
    chunk_text   text NOT NULL,
    embedding    vector(768) NOT NULL,                    -- локальная модель nomic-embed-text
    search_tsv   tsvector GENERATED ALWAYS AS (to_tsvector('russian', chunk_text)) STORED,
    token_count  integer NOT NULL DEFAULT 0,
    created_at   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_liability_chunk UNIQUE (norm_id, chunk_index),
    CONSTRAINT ck_liability_chunk_index CHECK (chunk_index >= 0)
);
CREATE INDEX ix_liability_chunk_hnsw
    ON legal.liability_norm_chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX ix_liability_chunk_fts
    ON legal.liability_norm_chunks USING gin (search_tsv);
CREATE INDEX ix_liability_chunk_norm ON legal.liability_norm_chunks (norm_id);

CREATE TABLE legal.mapping_candidates (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id            uuid NOT NULL,
    duty_id           text NOT NULL,
    liability_node_id text NOT NULL,
    source            text NOT NULL CHECK (source IN ('vector','fulltext','umbrella','merged')),
    search_score      double precision NOT NULL,
    rank              integer NOT NULL,
    chunk_id          uuid REFERENCES legal.liability_norm_chunks(id) ON DELETE SET NULL,
    payload           jsonb NOT NULL,                    -- снимок нормы, который видел арбитр
    created_at        timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_mapping_candidate UNIQUE (run_id, duty_id, liability_node_id)
);
CREATE INDEX ix_mapping_candidates_run ON legal.mapping_candidates (run_id, duty_id);
CREATE INDEX ix_mapping_candidates_duty ON legal.mapping_candidates (duty_id, created_at DESC);

CREATE TABLE legal.llm_audit_log (
    id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id            uuid NOT NULL,
    duty_id           text NOT NULL,
    stage             text NOT NULL CHECK (stage IN ('hypothesis','retrieval','arbiter','validation')),
    model             text NOT NULL,
    temperature       numeric(3,2) NOT NULL DEFAULT 0 CHECK (temperature = 0),
    prompt_version    text NOT NULL,
    attempt           integer NOT NULL DEFAULT 1,
    request_json      jsonb NOT NULL,
    response_json     jsonb,
    error_text        text,
    duration_ms       integer NOT NULL DEFAULT 0,
    validation_result text NOT NULL CHECK (validation_result IN
                          ('valid','invalid','repaired','manual_review','skipped')),
    created_at        timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_llm_audit_duty ON legal.llm_audit_log (duty_id, created_at DESC);
CREATE INDEX ix_llm_audit_run ON legal.llm_audit_log (run_id, stage, attempt);

CREATE TABLE legal.duty_liability_mappings (
    id                     uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id                 uuid NOT NULL,
    duty_id                text NOT NULL,
    liability_node_id      text NOT NULL,
    match_degree           text NOT NULL CHECK (match_degree IN ('full','partial','blanket')),
    probability_score      numeric(4,3) NOT NULL CHECK (probability_score BETWEEN 0 AND 1),
    qualifying_conditions  jsonb NOT NULL DEFAULT '[]'::jsonb,
    sanction_summary       text NOT NULL,
    legal_reasoning        text NOT NULL,
    requires_human_review  boolean NOT NULL,
    hypotheses             jsonb NOT NULL DEFAULT '[]'::jsonb,
    model_version          text NOT NULL,
    prompt_version         text NOT NULL,
    audit_id               uuid REFERENCES legal.llm_audit_log(id) ON DELETE SET NULL,
    calculated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_duty_liability UNIQUE (duty_id, liability_node_id)
);
CREATE INDEX ix_duty_liability_run ON legal.duty_liability_mappings (run_id);
CREATE INDEX ix_duty_liability_review
    ON legal.duty_liability_mappings (requires_human_review, calculated_at DESC);
```

**Идемпотентность.** `run_id` — детерминированный UUID из SHA-256 канонического JSON
`(dutyId, duty fields, sorted candidate node ids, modelVersion, promptVersion)`.
Повторный запуск с теми же входами находит уже сохранённый `run_id` и возвращает
сохранённый результат, не вызывая LLM. При новой версии модели/промпта или другом
пуле `run_id` меняется; upsert по `(duty_id, liability_node_id)`support обновляет
существующую связь, не создавая дублей.

## 3. DTO и интерфейсы сервисов

File: `src/Csms.Application/LiabilityMappingContracts.cs`.

```csharp
public sealed record DutyIdentifiers(
    string DutyId,
    string? SourceDocumentId,
    string? Article,
    string? Clause,
    string? Item,
    string? ExternalId);

public sealed record DutyInput(
    string Statement,
    string Object,
    string Subject,
    string? Deadline,
    string? Condition,
    DutyIdentifiers Identifiers,
    string LiabilityActCode);

public enum ViolationKind
{
    Omission,
    DeadlineViolation,
    ProcedureOrFormViolation,
    SubjectOrAuthorityViolation
}

public sealed record ViolationHypothesis(
    string Id,
    ViolationKind Kind,
    string Text,
    string DutyId);

public enum LiabilityNormType { Special, General, Blanket, Procedural, Other }
public enum CandidateSource { Vector, FullText, Umbrella, Merged }

public sealed record LiabilityCandidate(
    string LiabilityNodeId,
    string NormReference,
    string Text,
    string ActCode,
    string ActVersion,
    LiabilityNormType NormType,
    string Status,
    DateOnly? EditionDate,
    IReadOnlyDictionary<string, string> Metadata,
    CandidateSource Source,
    double SearchScore,
    string? ChunkId = null);

public enum MatchDegree { Full, Partial, Blanket, Irrelevant }

public sealed record MatchedLiability(
    string LiabilityNodeId,
    string NormReference,
    MatchDegree MatchDegree,
    double ProbabilityScore,
    IReadOnlyList<string> QualifyingConditions,
    string SanctionSummary,
    string LegalReasoning,
    bool RequiresHumanReview);

public sealed record LiabilityMappingResult(
    string DutyId,
    Guid RunId,
    IReadOnlyList<ViolationHypothesis> Hypotheses,
    IReadOnlyList<MatchedLiability> MatchedNorms,
    int CandidateCount,
    string ModelVersion,
    string PromptVersion,
    DateTimeOffset CalculatedAt,
    bool RequiresHumanReview);

public sealed record LlmArbiterItem(
    string LiabilityNodeId,
    MatchDegree MatchDegree,
    double ProbabilityScore,
    IReadOnlyList<string> QualifyingConditions,
    string SanctionSummary,
    string LegalReasoning,
    bool RequiresHumanReview);

public sealed record LlmArbiterResponse(IReadOnlyList<LlmArbiterItem> Results);

public interface IViolationHypothesisGenerator
{
    IReadOnlyList<ViolationHypothesis> Generate(DutyInput duty);
}

public interface ILiabilityRetriever
{
    Task<IReadOnlyList<LiabilityCandidate>> RetrieveAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        CancellationToken cancellationToken = default);
}

public interface ILlmArbiter
{
    Task<LlmArbiterResponse> ArbitrateAsync(
        DutyInput duty,
        IReadOnlyList<ViolationHypothesis> hypotheses,
        IReadOnlyList<LiabilityCandidate> candidates,
        Guid runId,
        CancellationToken cancellationToken = default);
}

public interface IMappingValidator
{
    ValidationOutcome Validate(
        LlmArbiterResponse response,
        IReadOnlyList<LiabilityCandidate> candidates);
}

public sealed record ValidationOutcome(
    IReadOnlyList<MatchedLiability> Accepted,
    IReadOnlyList<string> Violations,
    bool NeedsManualReviewFallback);

public interface ILiabilityMappingStore
{
    Task<bool> HasCompletedRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task SaveCandidatesAsync(Guid runId, string dutyId, IReadOnlyList<LiabilityCandidate> candidates,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LiabilityCandidate>> GetCandidatesAsync(Guid runId, string dutyId,
        CancellationToken cancellationToken = default);
    Task SaveMappingsAsync(Guid runId, LiabilityMappingResult result,
        CancellationToken cancellationToken = default);
    Task<LiabilityMappingResult?> GetResultAsync(Guid runId, string dutyId,
        CancellationToken cancellationToken = default);
    Task WriteAuditAsync(LlmAuditEntry entry, CancellationToken cancellationToken = default);
}

public interface IDutyLiabilityMappingService
{
    Task<LiabilityMappingResult> MapAsync(DutyInput duty, CancellationToken cancellationToken = default);
    Task<LiabilityMappingResult?> GetAsync(string dutyId, CancellationToken cancellationToken = default);
}
```

## 4. Пайплайн

### Этап 1 — инверсия и расширение запроса

`ViolationHypothesisGenerator` — детерминированный, без LLM. Из позитивной обязанности строятся 4 гипотезы:

| Id | Вид | Шаблон |
|----|-----|--------|
| h1 | `Omission` | Полное неисполнение / бездействие: «{subject} не совершил(а) {object} ({statement})» |
| h2 | `DeadlineViolation` | Нарушение срока: «… нарушил(а) срок: {deadline ?? «установленный срок»}» |
| h3 | `ProcedureOrFormViolation` | Нарушение порядка/формы/состава сведений |
| h4 | `SubjectOrAuthorityViolation` | Нарушение субъектом / превышение полномочий |

Гипотезы сохраняются в результате и подаются на поиск и арбитру.

### Этап 2 — пул кандидатов (recall > precision)

`LiabilityRetriever`:

1. **Вектор**: `embedding <=> queryEmbedding`, `LIMIT 30`, только действующие нормы нужного акта.
2. **Полный текст**: `search_tsv @@ websearch_to_tsquery('russian', query)`, сортировка `ts_rank_cd`, `LIMIT 30`. Запрос собирается из объекта, субъекта, срока, действия и гипотез.
3. **Umbrella boost**: принудительно добавить топ-5 типов `general`/`blanket` того же акта — даже если они не попали в top-30 по сходству.
4. Объединить, дедуплицировать по `liability_node_id`, оставить максимальный score и все источники в `source=Merged`, отсортировать и урезать до **30**.
5. Если кандидатов меньше 25 — дотянуть за счёт общих норм и ослабленного FTS.
6. Сохранить снимок каждого кандидата в `mapping_candidates`.

### Этап 3 — арбитр и скоринг

`LlmArbiter`: батч ≤ 8 кандидатов и ≤ 24 000 символов (текст нормы режется до 1 200 символов с пометкой «…»).температура `0.0`, strict JSON, `max_tokens=2048`, таймаут 900 с, 3 попытки с экспоненциальной задержкой 2/4/8 с. Каждая попытка пишется в `llm_audit_log`. После трёх неудач для каждого кандидата батча создаётся консервативная запись с `requires_human_review=true` и объяснением в `legal_reasoning`.

### Правила валидации (`MappingValidator`)

- JSON должен разбираться в `LlmArbiterResponse` без未知 полей и без комментариев;
- каждое поле `liability_node_id` обязано быть в пуле кандидатов — иначе запись отклоняется;
- дубликаты `liability_node_id` дедуплицируются (оставляется вариант с максимальным score); конфликтующие оценки помечаются на ручную проверку;
- все кандидаты обязаны получить оценку; пропущенные — fallback на ручную проверку;
- `match_degree` только `full|partial|blanket|irrelevant`; `irrelevant` фильтруется из вывода, но остаётся в аудите;
- `probability_score` приводится к `[0,1]`; исходное значение вне диапазона = ошибка валидации → ручная проверка;
- `qualifying_conditions` — массив строк, может быть пустым; `null` = ошибка валидации;
- `sanction_summary` и `legal_reasoning` — непустые строки;
- `requires_human_review` выставляется в `true` при `probability<0.65`, `blanket`, конфликте, неполных метаданных нормы (нет статуса/даты редакции/реквизита), а также при любой ошибке валидации — даже если модель вернула `false`;
- при `full` и непустых `qualifying_conditions` проверяется, что условия не подменяют сам факт совпадения: модель обязана разделить формулировку «что совпало» и «pri каких условиях наступает ответственность».

## 5. Системный промпт LLM-арбитра

```
Ты — правовой арбитр по сопоставлению структурированной обязанности (Сущность А)
и норм об ответственности (Сущность Б). Твоя задача — оценить ТОЛЬКО тот пул
кандидатов, который передан в пользовательском сообщении.

КАТЕГОРИЧЕСКИЕ ЗАПРЕТЫ:
- не добавляй статьи, нормы, реквизиты, даты и санкции, которых нет во входных данных;
- не используй внешние знания и не «вспоминай»ivil кодекс/закон наизусть;
- не возвращай нормы с liability_node_id вне переданного пула;
- не выдумывай.norm_reference и текст нормы;
- не пиши ничего вне JSON.

На входе: duty (обязанность), hypotheses (3–4 гипотезы нарушения),
candidates (нормы-кандидаты с реквизитами и текстом).

Для КАЖДОГО кандидата определи:
1) match_degree — ТОЛЬКО факт совпадения состава нормы с гипотезой нарушения:
   - full — состав прямо и конкретно описывает неисполнение или ненадлежащее
     исполнение именно этой обязанности (совпадают действие/объект/субъект/срок);
   - partial — норма охватывает только один аспект обязанности либо требует
     дополнительных последствий, ущерба или иных условий;
   - blanket — специального состава нет, но обязанность подпадает под общую норму
     об ответственности за нарушение требований данного вида законодательства;
   - irrelevant — норма не относится к обязанности и исключается из итогового вывода.
2) probability_score — число от 0.0 до 1.0: насколько применима норма.
3) qualifying_conditions — массив строк: условия, при которых наступает
   ответственность (ущерб, вина, последствия, доп. факт, срок). ПУСТОЙ массив,
   если дополнительных условий нет. НЕ смешивай это с match_degree: match_degree —
   что совпало, qualifying_conditions — при чём именно наступает ответственность.
4) sanction_summary — кратко, только из текста нормы.
5) legal_reasoning — краткое обоснование со ссылкой на текст кандидата и гипотезу.
6) requires_human_review — true при низкой уверенности (<0.65), правовой
   неоднозначности, конфликте норм, неполных метаданных или если ты не уверен(а).

Ответ — ТОЛЬКО валидный JSON по схеме ниже, без пояснений вне JSON:
{
  "results": [
    {
      "liability_node_id": "< id из кандидатов >",
      "match_degree": "full|partial|blanket|irrelevant",
      "probability_score": 0.0,
      "qualifying_conditions": ["..."],
      "sanction_summary": "...",
      "legal_reasoning": "...",
      "requires_human_review": false
    }
  ]
}
```

## 6. JSON-схема ответа

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "type": "object",
  "additionalProperties": false,
  "required": ["results"],
  "properties": {
    "results": {
      "type": "array",
      "minItems": 1,
      "items": {
        "type": "object",
        "additionalProperties": false,
        "required": [
          "liability_node_id", "match_degree", "probability_score",
          "qualifying_conditions", "sanction_summary", "legal_reasoning",
          "requires_human_review"
        ],
        "properties": {
          "liability_node_id": { "type": "string", "minLength": 1 },
          "match_degree": { "enum": ["full", "partial", "blanket", "irrelevant"] },
          "probability_score": { "type": "number", "minimum": 0, "maximum": 1 },
          "qualifying_conditions": {
            "type": "array",
            "items": { "type": "string", "minLength": 1 }
          },
          "sanction_summary": { "type": "string", "minLength": 1 },
          "legal_reasoning": { "type": "string", "minLength": 1 },
          "requires_human_review": { "type": "boolean" }
        }
      }
    }
  }
}
```

## 7. Батчинг и повторные попытки

- Батч: 8 кандидатов, лимит 24 000 символов запроса, усечение текста нормы до 1 200 символов; длина проверяется до отправки.
- Повтор: 3 попытки на батч, задержки 2 с → 4 с → 8 с, только при transient-ошибках/невалидном JSON; `OperationCanceledException` не ретраится.
- После 3 попыток: `manual_review` fallback для каждого кандидата батча, аудит с `validation_result='invalid'`.
- Все попытки пишутся в `llm_audit_log` (запрос, ответ, ошибка, длительность, модель, температура, результат валидации).

## 8. Тесты

`tests/Csms.Tests/ComplianceLiabilityMappingTests.cs`: полное совпадение, частичное,
бланкетная норма, пустой пул, дубликаты, длинные тексты, конфликт норм,
невалидный JSON → retry, исчерпание попыток → manual review, норма вне пула,
probability вне диапазона, разделение факта и условий, повторный запуск без дублей.

## 9. Фактическая реализация и запуск

### Файлы

- `src/Csms.Application/LiabilityMappingContracts.cs` — DTO, интерфейсы pipelines и store.
- `src/Csms.Application/LiabilityMappingRules.cs` — версия промпта, пороги, `run_id`, батчинг.
- `src/Csms.Application/LiabilityArbiterPrompts.cs` — системный промпт и сборка user-prompt.
- `src/Csms.Application/LlmArbiterResponseParser.cs` — строгий разбор без «вырезания JSON».
- `src/Csms.Application/ViolationHypothesisGenerator.cs` — 4 детерминированные гипотезы.
- `src/Csms.Application/MappingValidator.cs` — все правовые инварианты вывода.
- `src/Csms.Infrastructure/Liability/` — Ollama-клиенты, гибридный ретривер, арбитр,
  оркестратор, EF/in-memory хранилища, `EmptyLiabilityRetriever` для memory-режима.
- `src/Csms.Infrastructure/Migrations/20261120041526_AddLegalLiabilityMapping.cs`
  (+ `.Designer.cs`) — схема `legal`, pgvector/HNSW, tsvector/GIN, все ограничения.
- `src/Csms.Api/LiabilityMappingEndpoints.cs` — `POST /api/liability-mapping/map`,
  `GET /api/liability-mapping/{dutyId}`.
- `src/Csms.Api/appsettings.json` — раздел `LiabilityMapping` (endpoint, model,
  embeddingModel, timeout, maxRetries, retryBaseDelayMs).

### Предпосылка: pgvector

Миграция выполняет `CREATE EXTENSION IF NOT EXISTS vector`. Если расширение не
установлено в кластере, миграция падает с `0A000: extension "vector" is not
available`. Это произошло на локальном кластере :5433 — DDL и код готовы, но для
живого прогона нужно установить pgvector (>=0.5 для HNSW) и повторить:

```powershell
dotnet ef database update --project src/Csms.Infrastructure --startup-project src/Csms.Api
```

В `EIS_STORE=memory` регистрируется `EmptyLiabilityRetriever`: эндпоинт работает,
возвращает `CandidateCount=0` и не вызывает модель.

### Проверки (2026-11-20)

- `dotnet build` — 0 ошибок (одно старое предупреждение xUnit2009 в unrelated-тесте).
- `dotnet test --no-build` — **96 passed / 3 skipped**, из них 17 новых тестов модуля.

### Пример запроса

```http
POST /api/liability-mapping/map
Content-Type: application/json

{
  "statement": "Подготовить отчёт",
  "object": "отчёт",
  "subject": "Начальник отдела",
  "deadline": "до 31.12.2026",
  "condition": "при наличии оснований",
  "identifiers": {
    "dutyId": "duty-1",
    "sourceDocumentId": "doc-1",
    "article": "15",
    "clause": "1",
    "item": "3",
    "externalId": "ext-1"
  },
  "liabilityActCode": "44-FZ"
}
```

`qualifying_conditions` всегда массив (может быть пустым); `irrelevant` в ответе
API не появляется, но остаётся в `llm_audit_log`; спорные связи приходят с
`requires_human_review=true`.
