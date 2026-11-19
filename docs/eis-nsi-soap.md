# Выгрузки справочников НСИ через getDocsIP (КТРУ)

Этот файл фиксирует **фактический** способ получения выгрузок после закрытия
открытого FTP. Всё, что помечено «из XSD», проверено по файлам каталога
`schemas/17.0/`; что помечено «по документации сервиса» — требует подтверждения
на тестовом контуре, потому что в XSD этого нет.

## Что изменилось

- Открытый FTP-сервер `ftp.zakupki.gov.ru` **закрыт с 1 января 2025 года**.
  Казначейство перевело отдачу открытых данных, включая КТРУ (`nsiKtru`),
  на веб-сервис интеграции.
- Рабочий сервис — SOAP `getDocsIP`, метод `getNsiRequest`. Адрес из
  документации: `https://int.zakupki.gov.ru/eis-integration/services/getDocsIP`;
  официальная WSDL из комплекта XSD 17.0 указывает `https://int44.zakupki.gov.ru/...`.
  Проверка 19.11.2026: `int44` сбрасывает TLS-handshake, а `int` отдаёт тот же
  WSDL `fz44/get-docs-ip` и работает — использовать `int`.
- Анонимный доступ (прежний логин `free`) больше не работает. Нужен
  персональный токен `individualPerson_token`, который физическое лицо
  генерирует в личном кабинете ЕИС после входа через ЕСИА (Госуслуги).
- На машине, откуда идут запросы, должны быть установлены **корневые
  сертификаты Минцифры России**. В тестовых скриптах SSL иногда отключают,
  но в проде так делать нельзя.
- В ответе приходят прямые ссылки на ZIP-архивы с XML, соответствующими
  актуальным XSD. Структура XML внутри архивов осталась прежней (как во
  времена FTP).

## Структура запроса по XSD

Источник: `schemas/17.0/GetDocsWS/GetDocsIndividualPerson/getDocsIP-ws-api.xsd`.
 targetNamespace — `http://zakupki.gov.ru/fz44/get-docs-ip/ws`,
 `elementFormDefault="unqualified"` (дочерние элементы без префикса).

`getNsiRequest` (line 687):

| Путь | Тип из XSD | Правило |
| --- | --- | --- |
| `index/id` | `base:guidType` (BaseTypes.xsd:2822) | строка 1..36 символов, UUID v4 подходит |
| `index/createDateTime` | `xs:dateTime` | ISO 8601, например `2026-11-19T12:00:00+03:00` |
| `index/mode` | `base:indexModeType` (BaseTypes.xsd:2848) | `PROD` или `TEST` |
| `selectionParams/nsiCode44` | `base:nsiAllListCodeType` (BaseTypes.xsd:3379) | строка 1..50, код из справочника `nsiAllList` |
| `selectionParams/nsiKind` | `nsiKindType` (line 979) | `all` — полная выгрузка, `inc` — инкремент |

`nsiCode44` — **свободная строка в XSD**, поэтому код КТРУ нельзя выдумывать:
сначала надо запросить `nsiAllList` (это допустимое значение `nsiCode44`) и
взять точный `nsiCode` оттуда. В документации встречается `nsiKtru`, но
окончательно значение подтверждается только ответом `nsiAllList`.

Ответ `getNsiResponse` (line 740) в `dataInfo` содержит одну из ветвей:

- `nsiArchiveInfo` (до 5000) → `archiveUrl` (`base:hrefType`, 1..1024) +
  `archiveName` (`fileNameType`);
- `noData` (fixed `true`) — справочник не найден;
- `errorInfo` — ошибка сервиса.

## Шаблон SOAP-запроса

Оболочка Envelope и способ передачи токена — **по документации сервиса**, в XSD
не описаны. Значения `nsiCode44`, `nsiKind`, `mode` и формат `id` — из XSD.

```http
POST https://int.zakupki.gov.ru/eis-integration/services/getDocsIP
Content-Type: text/xml; charset=utf-8
SOAPAction: "getNsiRequest"
individualPerson_token: <TOKEN>
```

```xml
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                  xmlns:eis="http://zakupki.gov.ru/fz44/get-docs-ip/ws">
  <soapenv:Body>
    <eis:getNsiRequest>
      <index>
        <id>3f2504e0-4f89-41d3-9a0c-0305e82c3301</id>
        <createDateTime>2026-11-19T12:00:00+03:00</createDateTime>
        <mode>PROD</mode>
      </index>
      <selectionParams>
        <nsiCode44>nsiKtru</nsiCode44>
        <nsiKind>all</nsiKind>
      </selectionParams>
    </eis:getNsiRequest>
  </soapenv:Body>
</soapenv:Envelope>
```

При скачивании каждого `archiveUrl` тот же токен передаётся в заголовке
`individualPerson_token`. Точное имя заголовка и значение `SOAPAction`
сверить с WSDL сервиса на доступном контуре.

## Регламент

- Полную выгрузку `all` запрашивать **не чаще одного раза в сутки**.
- Для актуализации между полными выгрузками использовать `inc`.
- Архив и его XML не надо «доделывать»: парсер читает теги по XSD и считает
  SHA-256 канонического снимка, как это уже делает `EisNsiTreeImporter`.

## Структура позиции КТРУ внутри XML

Источник: `schemas/17.0/IntegrationTypes.xsd`:

- `zfcs_nsiKTRUType` (line 52017) → `zfcs_KTRUPositionsType` (52037) →
  `position` (`zfcs_KTRUPositionType`, 52054) → `data`.
- В `data`: `code` (обязательный, `base:ktruCodeType`), `versionCode`,
  `version`, `inclusionDate`, `publishDate`, `updateDate`, `name`, `OKPD2`
  (обязательный, `zfcs_OKPDRef`), `status` (`ACTIVE` / `INACTIVE` /
  `TERMINATED`), `actual`, `applicationDateStart`, `applicationDateEnd`.

Упоминание каталога `fcs_nsi/nsiKTRUNew` в примечании к полю `actual`
(line 52122) — историческое: подсказка, что в старых FTP-выгрузках это поле не
заполнялось. Не считать этот каталог рабочим источником.

## Что блокирует реальный импорт КТРУ

1. ~~Соединение до `int44.zakupki.gov.ru:443` сбрасывается на `SSL_connect`.~~
   Снято 19.11.2026: с внешним IP `195.226.203.100` TCP открыт, но handshake
   всё равно рвётся; зато `int.zakupki.gov.ru:443` отдаёт тот же WSDL
   `fz44/get-docs-ip` и принимает ГОСТ-TLS + SOAP.
2. **Токен не принят ЕИС.** Перевыпущенный токен записан в User Secrets и
   корректно передаётся (в SOAP- и HTTP-заголовках, с `SOAPAction` по WSDL),
   но сервер по-прежнему отвечает «Токены для сервисов отдачи отсутствуют в
   ЕИС». Проверить в ЛК: выдан ли он именно для сервисов отдачи данных 44-ФЗ,
   активен ли и не относится ли к другому контуру.
3. Не подтверждён точный `nsiCode` из `nsiAllList` — запросить сразу после
   смены токена. Обмен на тестовом контуре с корневыми сертификатами Минцифры
   тоже не проверен.

Код импорта готов в `csms/eis-parser`: `BuildNsiRequest`, `NsiXmlParser`,
`NsiSnapshotBuilder`, `NsiImportService` и CLI `--import-nsi` (`--dry-run` — без
записи). Снимок публикуется в `POST /api/references/import`, токен в `csms-api`
не дублируется. Живой SOAP-запрос 19.11.2026 дошёл до ЕИС, но упёрся в токен;
после перевыпуска достаточно `--import-nsi --dry-run`, а затем обычного
`--import-nsi` для публикации.

Пока это не закрыто, запись `nsiKTRU / sse-073720` в postgres — демо, а не
выгрузка; шапка фронта его не показывает.
