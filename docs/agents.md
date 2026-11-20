# Карта агентов CSMS (справочник)

> Обновлено: 2026-11-20.
>
> Речь идёт о **программных агентах/воркерах системы**, а не об ассистентах-разработчиках
> (Koda, Google AI Studio и т.п.). Если нужно вспомнить «кто что делает и где живёт» —
> начинай отсюда. Брифы для следующих агентов лежат в `docs/tasks/`.

## Краткая таблица

| Агент | Тип | Что делает | Где | Статус |
| --- | --- | --- | --- | --- |
| **Разметка правил НПА** | фоновый `BackgroundService` | Каждый день в 03:00 МСК скачивает официальные акты, OCR → правила по ОКПД2, публикует версии | `csms-api`: `NpaSyncWorker`, `Csms.Infrastructure/Npa/` | Работает на 471-р, 1875, 2604; 926/927 добавлены в конфиг, разбор упирается в 500 портала |
| **LLM-аналитик НПА (второй агент)** | вызывается из синхронизатора + отдельный endpoint | Разбирает только спорные строки, ищет неявные коды, всё отправляет в `needs_review` | `csms-api`: `NpaLlmAnalyzer`, `NpaLlmResponseParser`, `NpaLlmRuleAdapter` | Реализован, локальная модель `qwen2.5:14b-instruct-q4_K_M`; подтверждение по ID правила |
| **Автообновление XSD ЕИС** | фоновый `BackgroundService` | Раз в 720 мин проверяет Альбом ЕИС, скачивает ZIP с XSD/WSDL, атомарно меняет `schemas/<версия>/` | `csms-api`: `EisSchemaUpdateWorker`, `EisSchemaDownloader` | Работает; через API видно `status`, принудительный `/update` |
| **Опрос ЕИС 44-FЗ `getDocsIP`** | отдельный `.NET 9` worker | Последовательно опрашивает регионы/подсистемы/даты, скачивает архивы, дедуплицирует документы | `csms/eis-parser/src/Eis44Parser.Worker/PollingWorker.cs` | Код и тесты готовы; live заблокирован токеном ЕИС/контуром (старый токен утёк в git) |
| **Импортёр справочников НСИ** | on-demand импортёр, не демон | Обходит публичное дерево ЕИС и публикует версию набора с SHA-256 | `csms-api`: `EisNsiTreeImporter`, `/api/references/import/eis/{dataset}` | ОКПД2 загружен: 21 066 кодов; КТРУ/адреса ждут выгрузок по токену |
| **Извлечение функций из документа** | конвейер Ingest→Map→Validate→Reduce→Curate (по запросу) | Сегментирует любой НПА/регламент/ТЗ, выделяет атомарные подзадачи и раскладывает по таксономии контрактной службы | `csms-api`: `DocumentExtractionService`, `Csms.Infrastructure/Documents/`, `DocumentFunctionsEndpoints.cs` | Реализован; 76 тестов зелёные; первый целевой документ — Приказ № 158н |
| **Compliance & Liability Mapping** | конвейер по запросу (4 шага) | Обязанность → гипотезы нарушения → гибридный пул норм → строгий LLM-арбитр → валидатор/аудит | `csms-api`: `DutyLiabilityMappingService`, `Csms.Infrastructure/Liability/`, `LiabilityMappingEndpoints.cs` | Реализован, 17 новых тестов зелёные; живой прогон ждёт pgvector в локальном PostgreSQL |

---

## 1. Агент разметки правил НПА по ОКПД2 (первый агент)

**Зачем:** автоматически разметить 21 066 кодов ОКПД2 по официальным актам, не выдумывая
коды и не угадывая спорные случаи. Источник текста — только
`http://publication.pravo.gov.ru` либо загруженная заказчиком копия `.pdf`/`.docx`.

**Где:**

- воркер — `src/Csms.Api/NpaSyncWorker.cs`;
- оркестратор — `src/Csms.Infrastructure/Npa/NpaSyncService.cs`;
- пайплайн — `PravoDocumentClient` → `NpaDocumentParser` (Docnet + Tesseract rus, две
  проекции OCR) → `NpaLayoutParser` → `Okpd2CodeResolver`/`Okpd2Catalog` →
  `NpaRuleClassifier` → `NpaRuleBuilder` → `NpaRuleHierarchy`;
- хранилища — `EfCoreNpaRuleStore` / `InMemoryNpaRuleStore`, таблицы `ref.npa_documents`
  и `ref.npa_rules`;
- API — `src/Csms.Api/NpaEndpoints.cs`;
- бриф — `docs/tasks/npa-rules.md`.

**Как работает:**

- старт — `Npa:CheckTime=03:00` МСК (UTC+3, без летнего времени), пауза между актами —
  `Npa:CheckDelayMinutes=30`. Акты идут **строго последовательно**.
- текст сравнивается по SHA-256; при изменении публикуется новая версия (`-r2`, `-r3`, …),
  старые версии не перезаписываются.
- код принимается только если он есть в активном снимке `nsiOKPD2`;
  `262011110` → `26.20.11.110` восстанавливается только при однозначном совпадении.
- самый длинный префикс побеждает, `exclude` сильнее `include` предка; унаследованные
  правила материализуются в БД.
- явные «кроме кодов …» дают `exclude`; «кроме танкеров/мебели» внутри наименования —
  уточнение `include`. Спорное → `needs_review`.
- `LastCheckedAt` (последняя сверка) отделён от `FetchedAt` (время публикации версии);
  сверка пишется даже при `up_to_date` и при ошибке.

**Основные endpoints:**

- `GET /api/npa/config` — акты, расписание, блок `llm`;
- `GET /api/npa/documents` / `/{code}` — версии и активная версия;
- `GET /api/npa/documents/{code}/rules?directOnly=true&skip=&take=` — правила;
- `GET /api/npa/documents/{code}/effective?okpd2Code=...` — действующее правило;
- `POST /api/npa/documents/{code}/sync?force=true&dryRun=true` — переразбор;
- `POST /api/npa/documents/{code}/upload` — разметка своей копии;
- `POST /api/npa/documents/{code}/rules/{ruleId}/verify` — решение человека по конкретной
  записи (важно: **по ID**, не по коду — записей с одним кодом может быть несколько).

**Состояние (2026-11-20):**

- 471-р — `pravo-0001201603230014-r7`, 17 215 правил, 107 прямых, 88 `unresolved`;
- 1875 — `pravo-0001202412250018-r2`, 18 770 правил, 973 прямых, 1 503 `unresolved`;
- 2604 — `pravo-0001202201040005-r2`, 12 499 правил, 58 прямых, 387 `unresolved`;
- **926** (ПП РФ от 02.09.2015 № 926) и **927** — добавлены в `Npa:Documents`. 926 нужен
  для флага «ОКПД2 нормируется / не нормируется» (`TableLayout=true`), 927 — ценовые
  правила, перечня кодов ОКПД2 там нет (`TableLayout=false`). Живой `dryRun` пока падает
  из-за нестабильного `500.30` портала; в `Program.cs` выставлен `Accept-Encoding:
  identity`, в `PravoDocumentClient` добавлены повторы на сетевые ошибки и `5xx`.

---

## 2. Второй агент НПА — LLM-аналитик

**Отличие от первого:** не публикует правила молча. Любое предложение сначала попадает в
очередь `needs_review` и становится эффективным только после human-подтверждения.

**Где:** `src/Csms.Infrastructure/Npa/NpaLlmAnalyzer.cs`, `NpaLlmResponseParser.cs`,
`NpaLlmRuleAdapter.cs`; вызов — из `NpaSyncService` по `parsed.Unresolved`; ручной запуск —
`POST /api/npa/documents/{code}/analyze`.

**Модель и конфиг:** локальный Ollama, OpenAI-совместимый endpoint
`http://localhost:11434`, модель `qwen2.5:14b-instruct-q4_K_M` (~8.4 ГБ GGUF, ~10.7 ГБ
VRAM из 12), `temperature=0`, `response_format=json_object`, `BatchSize=5`,
`MaxCandidates=5`, `TimeoutSeconds=180`. Ключей и внешней сети нет. 32B не влезает в
RTX 3060 12 ГБ; стартовая `qwen2.5-coder:7b` плохо различала юридические оговорки.

**Защита от галлюцинаций:**

- код берётся только из списка кандидатов конкретной строки (поиск по активному ОКПД2);
- `source_quote` обязан дословно присутствовать в этой же строке (нормализация пробелов
  и регистра);
- не больше одного результата на строку;
- `implicit` всегда получает `requires_human_review=true`;
- все предложения становятся отдельными записями со статусом `needs_review`, даже если
  код уже есть у первого агента; явное правило не перезаписывается;
- наследование по дереву не идёт от `needs_review`, если для кода есть проверенное правило;
- подтверждение — `POST .../rules/{ruleId}/verify`, статус меняется только у выбранной
  записи.

**Результат сравнения на 471-р (63 спорные строки):**

| Модель | Принято | Отброшено | explicit / implicit | На проверке | Время |
| --- | --- | --- | --- | --- | --- |
| `qwen2.5-coder:7b` | 57 | 8 | 56 / 0 | 0 | — |
| `qwen2.5:14b-instruct-q4_K_M` | 31 | 19 | 21 / 10 | 11 | 216 с |

**Ограничение:** ~16 с на батч из 5 строк; для 1875-р (1 503 строки) это ~80 мин. Ночной
цикл остаётся последовательным, пауза 30 мин между актами сохраняется.

⚠️ В началах `docs/tasks/npa-rules.md` и `docs/tasks/npa-llm-agent.md` остались stale-строки
«LLM пока не реализована» — фактически агент реализован, см. §43 `.koda/PROJECT_STATE.md`
и текст выше.

---

## 3. Агент автообновления XSD Альбома ЕИС

**Где:** `src/Csms.Api/EisSchemaUpdateWorker.cs`, `EisSchemaDownloader.cs`,
`EisSchemaEndpoints.cs`, настройки — `EisSchemas:Update` в `appsettings.json`.

**Что делает:** первая проверка при старте, дальше каждые `CheckIntervalMinutes=720`
минут. Источник — публичная страница документов ЕИС (`sectionId=432`, ZIP «Интеграционные
схемы ЕИС версии 17.0»); скачивание требует `Referer` на страницу списка.

Алгоритм: найти newest `id`/версию → скачать ZIP → проверить лимиты/магику/пути →
распаковать → `EisSchemaCatalog.CheckDirectory` → атомарно подменить `schemas/<версия>/`
и сбросить кеш. При `AutoPromote=false` архив только готовится в `.incoming/`.

**Защиты:** HTTPS-only, лимиты архива и распаковки, отказ от `..`, компиляция
`XmlSchemaSet` до подмены, откат к старой версии при ошибке, манифест с id и SHA-256
против повторной загрузки. Официальный дефект `getDocsLE-ws-api.xsd` (лишний `>` после
`<?xml …?>`) загрузчик чинит точечно и пишет warning.

**API:** `GET /api/eis/schemas/status`, `POST /api/eis/schemas/update`.

**Статус:** работает, 32 XSD версии 17.0 компилируются; live `/update` давал
`Installed`, затем `UpToDate`. Схем для исполнения контракта и СМП (658/215н, 918) в
архиве нет — `PlaceholderXmlBuilder` остаётся `placeholder-v0`.

---

## 4. Агент опроса ЕИС 44-ФЗ (`getDocsIP`)

**Где отдельный solution:** `csms/eis-parser/Eis44Parser.sln` (.NET 9, четыре проекта).
Основной воркер — `src/Eis44Parser.Worker/PollingWorker.cs`.

**Состав:**

- `Eis44Parser.Core` — `SoapRequestBuilder` (жёсткий `xs:sequence`, токен в
  `soapenv:Header`), `Eis44Client` (SOAP POST + GET ZIP через локальный Stunnel на
  `127.0.0.1:8080`, Polly-повторы 429/5xx/сетевых ошибок), `TenderXmlParser`,
  коды подсистем `PRIZ`/`RPEC`/`RPGZ`/`NSI`;
- `Eis44Parser.Data` — EF Core, дедупликация по `PurchaseNumber` + версии;
- `Eis44Parser.Worker` — последовательный опрос по регионам, типам документов,
  подсистемам и датам (лимит ~8 RPS), CLI `--once`, `--validate`, `--check`;
- `Eis44Parser.Tests` — 5 xUnit-тестов.

**Запуск/проверка:**

```powershell
dotnet build eis-parser/Eis44Parser.sln
dotnet test eis-parser/Eis44Parser.sln
dotnet run --project eis-parser/src/Eis44Parser.Worker -- --validate
dotnet run --project eis-parser/src/Eis44Parser.Worker -- --check
```

**Статус:** код, тесты и readiness-проверки готовы. Live-обмен упирается не в код:
`int44.zakupki.gov.ru` сбрасывает ГОСТ-TLS; найден рабочий `int.zakupki.gov.ru:443`
(тот же WSDL `getDocsIP`), SOAP доходит, но ЕИС отвечает «Токены для сервисов отдачи
отсутствуют в ЕИС». Старый токен остался в публичной истории git (`eecaca5`) — подлежит
отзыву. PIN/токен живут только в User Secrets / env (`Eis44__IndividualPersonToken`) и
во временном конфиге `%TEMP%\eis44-stunnel.conf`. `nsiCode` для КТРУ пока не подтверждён
(`nsiKtru` — предположение).

---

## 5. Импортёр справочников НСИ (смежный автоматизм)

Не фоновая служба, а импорт по запросу, но его легко принять за «агента справочников».

**Где:** `EisNsiTreeImporter`, `POST /api/references/import/eis/{dataset}` и
`POST /api/references/import`; хранилище `ref.datasets` / `ref.items`; контракт —
`docs/tasks/reference-data.md`.

**Что делает:** обходит публичное дерево ЕИС
(`https://zakupki.gov.ru/epz/nsi/okpd2/children.html?parentId=…`) в ширину, сохраняет
полный узел в `payload`, считает SHA-256 канонического снимка и публикует версию
`eis-nsiOKPD2-<16 hex>`. Повтор той же версии даёт `400`. Есть SSE
`GET /api/references/events` с `ready` и `references-updated`.

**Результат:** активная версия ОКПД2 — `eis-nsiOKPD2-7c7ee4215e3ee405`, **21 066**
записей. КТРУ и адреса по-прежнему только демо-сиды в `db.json`: публичного дерева
КТРУ нет, SOAP требует токен; FTP закрыт с 01.01.2025.

---

## 6. Агент извлечения функций из документа (третий LLM-агент)

**Отличие от агентов НПА:** не ищет коды ОКПД2, а разбирает произвольный
нормативный/организационный документ на функции и атомарные подзадачи контрактной
службы. Работает **по запросу**, без фонового воркера, чтобы 14B не висела в VRAM.

**Где:** `src/Csms.Infrastructure/Documents/` (сегментер, экстрактор текста, Map,
Validate, Reduce, in-memory store), оркестратор `DocumentExtractionService`,
эндпоинты `src/Csms.Api/DocumentFunctionsEndpoints.cs`, контракты —
`src/Csms.Application/DocumentFunctionContracts.cs`, бриф —
`docs/tasks/document-functions-agent.md`.

**Конвейер:**

1. `DocumentTextExtractor` — `.docx`/`.pdf`/`.txt`/`.md`; PDF идёт через Docnet +
   Tesseract rus (тот же OCR, что и у агента НПА).
2. `DocumentSegmenter` — детерминированная иерархия «раздел → пункт → подпункт»,
   стабильные `segment_id` (`p-3-2-2-2`), `source_path`, цепочка `ancestors` с
   текстами родителей, дробление абзацев >2000 символов по предложениям.
3. Map — локальная `qwen2.5:14b-instruct-q4_K_M`, `temperature=0`,
   `response_format=json_object`, `max_tokens=2048`, таймаут 900 с, 1 сегмент на
   запрос (контекст модели 8192 токенов), массив `results` в ответе. Промпт прямо
   запрещает подставлять текст по памяти: модель знает 158н наизусть и ошибалась
   в цитатах, пока не добавлен few-shot пример и напоминание про `segment_text`.
4. Validate — код проверяет, что цитата есть в сегменте, а срок/роль/условие —
   в сегменте или в заголовках-предках; иначе `null` + `needs_review`.
5. Reduce — дедупликация по нормализованному ключу `(глагол, объект, роль,
   обязательность, условие)`; сохраняются все `source_paths`, `merge_reason`.
6. Curate — все результаты стартуют в `needs_review`; подтверждение по ID:
   `POST /api/documents/functions/{id}/verify`.

**API:** `GET /api/documents/config`, `POST /api/documents/extract?dryRun=true`,
`GET /api/documents/{sha256}/functions`, `GET /api/documents/{sha256}/report`,
`POST /api/documents/functions/{id}/verify`.

**Состояние (2026-11-20):** реализован, `dotnet test` — 79 passed / 3 skipped.
Эталон для приёмки — `tests/Csms.Tests/Fixtures/order158n-golden.json` (41 пункт).
Рабочий пул задач теперь лежит в БД и отдаётся `GET /api/order158n/pool` (см. §7),
фронтовый `order158nTaskPool.ts` — только чистые функции сборки маршрута.
Ограничения: хранилище результатов извлечения in-memory, граф зависимостей и
нечёткие пары Reduce не сделаны; живой прогон 158н пока покрывает 8/41 пункт.

---

## 7. Пул задач по Приказу № 158н как версионируемый справочник

**Зачем:** 41 пункт типового маршрута контрактной службы раньше был жёстко
зашит в `csms/src/services/Tasks/order158nTaskPool.ts` и дублировался в
 эталонной фикстуре бэка. Теперь единственный рабочий источник — активная версия
справочника `order158nPool` в `EIS_STORE` (memory или PostgreSQL, схема `ref`).

**Где:** контракты `src/Csms.Application/Order158nPoolContracts.cs`, каталог
`src/Csms.Infrastructure/Order158nPoolCatalog.cs`, эталон первичного заполнения
`src/Csms.Infrastructure/Seeds/order158n-pool.json`, эндпоинты
`src/Csms.Api/Order158nPoolEndpoints.cs`.

- Первый запуск импортирует эталон из официального приказа (SHA-256 файла), дальше
  чтение идёт только из `IReferenceDataStore`; версия не перезаписывается.
- `GET /api/order158n/pool` — активная версия, 41 пункт, реквизиты источника;
  `GET /api/order158n/pool/{id}` — поиск по ID или номеру пункта (`п. 3.1.1`).
- Каталог виден в `/api/references/datasets` как `order158nPool`; пул используется
  TasksPage, TaskAssignModal, TaskDetailPage, MyTasks, ContractServiceForm и
  `regulatoryDeadlines` через один react-query хук `useOrder158nPool()`.
- `publishedAt` эталона приводится к UTC: Npgsql не принимает `DateTimeOffset`
  с ненулевым offset для `timestamp with time zone`.
- чтение идёт через `IReferenceDataStore.GetAllWithPayloadAsync`: обычный
  `GetAllAsync` не выбор `PayloadJson` (там снимок ОКПД2 на десятки МБ), а пул
  хранит каждый пункт целиком именно в `payload`. В memory-режиме метод — алиас
  на `GetAllAsync`, в PostgreSQL — отдельный запрос с колонкой `PayloadJson`.

---

## 8. Compliance & Liability Mapping (четвёртый LLM-агент)

**Зачем:** связать структурированную обязанность (Сущность А) со всеми релевантными
нормами ответственности (Сущность Б) — один ко многим, без выдуманных статей
и без потери общих/бланкетных составов.

**Где:** `src/Csms.Application/LiabilityMapping*.cs`,
`ViolationHypothesisGenerator.cs`, `LlmArbiterResponseParser.cs`,
`MappingValidator.cs`; `src/Csms.Infrastructure/Liability/`;
`src/Csms.Api/LiabilityMappingEndpoints.cs`; бриф —
`docs/tasks/compliance-liability-mapping.md`.

**Конвейер:**

1. `ViolationHypothesisGenerator` — без LLM, 4 гипотезы: полное неисполнение,
   нарушение срока, нарушение формы/порядка, нарушение субъектом/полномочий.
2. `EfCoreLiabilityRetriever` — pgvector `vector(768)` + HNSW, `tsvector('russian')`
   + GIN, принудительный umbrella-пул общих/бланкетных норм; дедупликация до 30.
3. `LlmArbiter` — локальная `qwen2.5:14b-instruct-q4_K_M`, `temperature=0`,
   `response_format=json_object`, батч ≤8 и ≤24 000 символов, 3 попытки
   (2/4/8 с); каждая попытка пишется в `legal.llm_audit_log`.
4. `MappingValidator` — только id из пула, `irrelevant` отфильтровывается,
   `match_degree` отделён от `qualifying_conditions`, score 0..1, спорное →
   `requires_human_review`.

**API:** `POST /api/liability-mapping/map`, `GET /api/liability-mapping/{dutyId}`.
Повтор с теми же входами возвращает сохранённый результат по детерминированному
`run_id`, не дёргая модель и не создавая дубли.

**Особенность:** `legal.liability_norm_chunks.embedding vector(768)` с HNSW;
миграция требует установленного pgvector >=0.5. На локальном кластере :5433
расширения нет, поэтому `dotnet ef database update` пока завершается ошибкой с
`extension "vector" is not available` — это инфраструктурная предпосылка,
не ошибка кода. В `EIS_STORE=memory` пул пуст и LLM не вызывается.

**Проверки:** `dotnet build` 0 ошибок, `dotnet test --no-build` — 96 passed /
3 skipped (17 новых).

---

## Одноразовые брифы (не запущенные процессы)

Это задачи, которые мы выдавали агентам-исполнителям; пригодятся, чтобы не переписать
решение заново:

- `docs/tasks/npa-rules.md` — первый агент НПА (почти всё закрыто, см. §1);
- `docs/tasks/npa-llm-agent.md` — второй агент НПА (закрыто, см. §2);
- `docs/tasks/reference-data.md` — справочники НСИ (ОКПД2 закрыт, КТРУ/адреса ждут);
- `docs/tasks/postgres-persistence.md` — сохраняемость пакетов ЕИС в PostgreSQL
  (`EIS_STORE=postgres`, миграции, оптимистичная блокировка);
- `docs/tasks/document-functions-agent.md` — агент: сегментация → Map →
  Validate → Reduce → проверка человеком (реализован, см. §6); первый целевой
  документ — Приказ № 158н;
- `docs/eis-nsi-soap.md` — шаблон запроса `getNsiRequest` и разбор архивов НСИ;
- `docs/tasks/contract-smp.md` — серверная часть привлечения СМП/СОНКО:
  `/api/contract-smp-records`, машина состояний, правила 44-ФЗ/№ 1466,
  таблицы `contract.contract_smp_*` (2026-11-20);
- `docs/eis-open-questions.md` — открытые вопросы по обмену с ЕИС.

## Быстрые точки входа

```powershell
# все три процесса: Vite :5173, json-server :3001, csms-api :5099
npm start

# только API (НПА и справочники)
EIS_STORE=postgres ASPNETCORE_ENVIRONMENT=Development `
  dotnet run --project src/Csms.Api --no-build --no-launch-profile

# проверки
dotnet build src/Csms.Api --no-restore
dotnet test tests/Csms.Tests --no-restore
```
