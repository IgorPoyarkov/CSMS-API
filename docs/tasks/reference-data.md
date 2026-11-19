# Задача для агента: справочники НСИ из официальных выгрузок

Бриф для фронтенд-агента и агента, который будет писать парсер официальных
документов. Прочитать до `AGENTS.md` соседнего репозитория `csms` и
`CurrentTasks.md` (Tasks 16). Вопросы по контракту — заказчику, а не самовольные
изменения полей.

## Зачем

Сейчас ОКПД2 (143 записи), КТРУ (6) и адреса живут в `csms/db.json` как демо-моки.
Это не продакшн-источник: нет версии, источника, даты действия и доказательства,
что значения взяты из официальной выгрузки. Бэкенд добавил версионируемое
хранилище и API; парсер должен присылать туда настоящие данные, а фронт —
перестать считать `db.json` источником истины.

## Что важно понять про XSD

В скачанном Альбоме ЕИС 17.0 **есть структуры НСИ, но нет самих значений**:

- `schemas/17.0/fcsExport.xsd`: `nsiOKPD2List` (line 1416),
  `nsiOKPD2RequirementList` (1426), `nsiKTRUs` (1696), `nsiRightSideKTRUs` (1701),
  `nsiKLADRList` (2196), `nsiOKTMOList`, `nsiOKTMOPPOList`.
- `schemas/17.0/IntegrationTypes.xsd`: `zfcs_nsiOKPD2Type` (line 55263) — поля
  `id`, `parentId`, `code`, `parentCode`, `name`, `comment`, `actual`;
  `zfcs_nsiOKPD2RequirementType` (55317) — `versionCode`, `version`, `code`,
  `name`, `groupNumber`, `prohibition`, `restriction`, `industrial`,
  `lastUpdateDate` (это структура нацрежима ПП РФ № 1875 для `pp1875.ts`);
  `zfcs_nsiKTRUType` (52017) → `zfcs_KTRUPositionsType` → `data/code`,
  `data/versionCode`, `data/version`, `data/inclusionDate`;
`zfcs_nsiKLADRType` (61758) — `code`,
  `parentCode`, `objectType` (`R`, `D`, `A`, `C`, `S`, `L`), `fullName`,
  `shortName`, `OKATO`.
- `schemas/17.0/GetDocsWS/GetDocsIndividualPerson/getDocsIP-ws-api.xsd`,
  `getNsiRequest` (line 687+): `index` (`id`, `createDateTime`, `mode` =
  `PROD`/`TEST`) и `selectionParams` (`nsiCode44` из перечня `nsiAllList`,
  `nsiKind` = `all` — полная выгрузка, `inc` — инкремент); ответ
  `getNsiResponse` содержит `nsiArchiveInfo/archiveUrl` + `archiveName`.
  Аналогичный метод есть в `getDocsMIS-ws-api.xsd` (line 733+) и
  `getDocsLE-ws-api.xsd` (line 885). Значения приходят **архивами НСИ**, а не
  внутри XSD. Подробный шаблон — `docs/eis-nsi-soap.md`.

**Источники значений (в приоритете):** официальный сайт ЕИС через его
публичные endpoint-ы дерева НСИ, SOAP `getNsiRequest` сервиса `getDocsIP`,
затем официальные документы/таблицы (ОК 034-2014 ОКПД2, КТРУ, КЛАДР/ГАР/ФИАС,
перечень ПП РФ № 1875). Придумывать коды и поля запрещено.

Открытый FTP `ftp.zakupki.gov.ru` **закрыт с 1 января 2025 года**: Казначейство
перевело отдачу открытых данных, включая КТРУ, на SOAP `getDocsIP`. Упоминания
каталогов `fcs_nsi/...` в XSD — исторические, а не рабочий источник. Для
`getNsiRequest` нужен персональный токен `individualPerson_token` из личного
кабинета ЕИС (ЕСИА/Госуслуги): анонимный доступ под логином `free` больше не
работает. Токен передаётся и в SOAP-запросе, и в заголовке при скачивании
архива; на машине должны быть установлены корневые сертификаты Минцифры России.
Полную выгрузку `all` запрашивают не чаще раза в сутки, для обновлений — `inc`.
Пока токена нет, рабочий путь — публичное дерево НСИ на `zakupki.gov.ru`
(реально загружен ОКПД2); КТРУ ждёт токена, демо-запись выгрузкой не является.

### Найденный рабочий источник ОКПД2

Сайт ЕИС отдаёт дерево справочника без авторизации:

- `GET https://zakupki.gov.ru/epz/nsi/okpd2/children.html?parentId=0` — корень;
- для каждой папки — `...children.html?parentId=<key>` (тот же вызов, который
  делает карточка закупки через `okdpOkved.js`, `onLazyRead`);
- ответ — массив узлов `{ key, isFolder, code, name, ... }`.

Бэк обходит дерево в ширину (8 параллельных запросов, предел 12 уровней и
50 000 узлов), сохраняет каждый узел целиком в `payload`, считает SHA-256
канонического снимка и делает версию `eis-nsiOKPD2-<16 hex из SHA-256>`.
Повторный импорт того же состояния даёт ту же версию и корректно получает `400`.

Публичный KLADR через то же дерево **не импортируем**: адресных узлов сотни
тысяч, синхронный обход упирается в таймауты. Для адресов нужна отдельная
потоковая выгрузка ФИАС/ГАР (задача следующего этапа).

## Что уже сделано на бэке

## Что уже сделано на бэке

- `Csms.Application/ReferenceDataContracts.cs` — контракты и нормализатор.
- `Csms.Infrastructure`: `ref.datasets` (код, версия, источник, URL, SHA-256,
  дата публикации, дата импорта, счётчик, хеш набора, флаг активности) и
  `ref.items` (код, родитель, имя, actual, полный `jsonb payload`). Старые
  версии не удаляются; уникальный индекс с фильтром допускает ровно одну
  активную версию на справочник. Публикация — в транзакции: сначала снятие
  флага, затем вставка новой версии со всеми записями.
- `EIS_STORE=memory` (по умолчанию) → `InMemoryReferenceDataStore`;
  `EIS_STORE=postgres` → `EfCoreReferenceDataStore` (схема `ref`, миграция
  `AddReferenceDatasets`). Строка подключения — `ConnectionStrings:EisDb` в
  user-secrets, порт 5433, база `csms_eis`.
- `GET /api/references/datasets` — список всех версий с `hash` и `isActive`.
- `GET /api/references/{dataset}/items?search=...&parentCode=...&take=50` —
  поиск по **активной** версии (по коду и имени, регистронезависимо).
- `POST /api/references/import` — установка новой версии из выгрузки.
- `POST /api/references/import/eis/{dataset}?dryRun=true` — бэк сам скачивает
  официальный справочник из ЕИС, считает версию и (без `dryRun`) публикует его.
  `dryRun` возвращает объём, версию, SHA-256 и первые 5 записей без записи в БД.
  Сейчас поддерживается `nsiOKPD2`.
- `GET /api/references/events` — SSE: `event: ready` со списком наборов и
  `event: references-updated` с `{ dataset, fromVersion, toVersion, itemCount,
  hash, importedAt }`.

## Контракт импорта

`POST /api/references/import` (`application/json; charset=utf-8`):

```json
{
  "dataset": "nsiOKPD2",
  "version": "2026-11-01",
  "sourceDocument": "ОК 034-2014 (КПЕС 2008) — ОКПД2",
  "sourceUrl": "https://...",
  "sourceSha256": "<64 hex>",
  "publishedAt": "2026-11-01T00:00:00Z",
  "items": [
    {
      "code": "26.20.11.110",
      "parentCode": "26.20.11",
      "name": "Компьютеры",
      "actual": true,
      "payload": { "code": "26.20.11.110", "...": "полный объект из выгрузки" }
    }
  ]
}
```

- `dataset` — код из «Перечня справочников ЕИС» (`nsiOKPD2`, `nsiKTRU`,
  `nsiKLADR`, `nsiOKPD2Requirement`, `nsiOKTMO`, …). Код свободный, но должен
  совпадать с тем, чем будет искать фронт.
- `version` — версия выгрузки; повторная отправка той же пары `(dataset,
  version)` даёт `400`. Новый импорт делает новую версию активной атомарно.
- `sourceSha256` — SHA-256 исходного файла/архива, откуда взяты данные.
  Обязателен для настоящих выгрузок (в демо-сидах допустим свой текст).
- `code`/`parentCode`/`name`/`actual` можно не дублировать: нормализатор сам
  найдёт их в `payload` (в том числе `OKPD2: { code }` у КТРУ и `actual: true`).
  Но чем яснее парсер, тем меньше сюрпризов.
- `payload` сохраняется целиком как `jsonb`; ответ отдаёт поле `payloadJson`
  **строкой** — фронт делает `JSON.parse`. Ничего не «оптимизировать» на месте:
  сегодня не нужные поля понадобятся мапперу завтра.

## Что сделать агенту парсера

1. Парсить официальную выгрузку/документ, не придумывать значения. Для каждой
   записи сохранить полный объект в `payload` и указать `sourceSha256` исходника.
2. Отправлять `POST /api/references/import` с уникальной версией (дата выгрузки
   или id archived package). При падении импорта активной остаётся прежняя
   версия — повторять импорт с той же версией можно, пока она не зафиксирована.
3. Для КТРУ связывать позицию с ОКПД2 через `OKPD2/code`; для адресов сохранять
   `objectType` и `OKATO`; для ПП РФ № 1875 — `prohibition`/`restriction`/
   `industrial` из `nsiOKPD2Requirement`.
4. Не класть токены/ PIN в код и коммиты: токен ЕИС живёт в user-secrets.

## Что сделать фронту после готовности выгрузок

- Переключить `Okpd2Ref`/`KtruRef` и адресные справочники с `db.json` на
  `/api/references/{dataset}/items`; текущие 143/6 записей оставить демо-сидами.
- Подписаться на `/api/references/events`; на `references-updated` обновить
  React Query и **показать человеку diff версий/записей**. Автоматически менять
  мапперы и правила нацрежима нельзя — решение принимает человек.

## Живой импорт (2026-11-19)

Выполнено с `EIS_STORE=postgres`, база `csms_eis` на порту 5433:

- `POST /api/references/import/eis/nsiOKPD2?dryRun=true` → 21 066 узлов,
  версия `eis-nsiOKPD2-7c7ee4215e3ee405`,
  SHA-256 `7C7EE4215E3EE405D5770F00965423FD8E130CE42C482C94F9358272BBB2AB9A`.
- `POST /api/references/import/eis/nsiOKPD2` → активная версия заменила демо
  `smoke-1`, событие `references-updated`, `itemCount = 21066`.
- `GET /api/references/datasets` показывает активную версию с источником
  `https://zakupki.gov.ru/epz/nsi/okpd2/`; `GET .../items?search=Компьютер`
  и `?parentCode=01` возвращают реальные коды и имена.
- Повторный импорт той же версии → `400` (дубль отклонён).

КТРУ и адреса по-прежнему требуют отдельных выгрузок: на сайте ЕИС публичного
дерева КТРУ нет, а `fcs_nsi` недоступен. Старые демо-записи `nsiKTRU/sse-*` в
БД — не продакшн-данные.

## Проверки бэка (2026-11-19)

`dotnet build` — 0 ошибок; `dotnet test` — 39 пройдено, 2 пропущены без
`EIS_TEST_DB`. Живой прогон с `EIS_STORE=postgres`: импорт → поиск → список
наборов, SSE `ready` и `references-updated`; `scripts/smoke.ps1` — 14/14
(добавлены импорт, поиск и список версий справочника); миграция
`AddReferenceDatasets` применена к локальной базе на 5433.
