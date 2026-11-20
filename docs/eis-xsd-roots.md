# Глобальные корневые элементы пакетов ЕИС 17.0

Документ фиксирует интерфейс бэк-потока для фронтовых превью и будущего
доменного маппера (`Б1`). Источник — закоммиченные XSD
`schemas/17.0/fcsIntegration.xsd`, версия Альбома 17.0
(`create date 17.11.2026`). Ссылки на строки — именно по этому файлу.

**Важно.** Все перечисленные элементы лежат в пространстве имён
`http://zakupki.gov.ru/oos/integration/1` (атрибут `targetNamespace` в строке 3,
`elementFormDefault="qualified"`). Превью без этого `xmlns` не пройдёт
XSD-валидацию, даже если имя корня совпадает.

## Таблица корней по типам документов CSMS

| Документ CSMS | Глобальный корень | Тип дочернего `data` | Строка в `fcsIntegration.xsd` |
|---|---|---|---:|
| План-график (ПГ) | `tenderPlan2020` | `tp:tenderPlan2020Type` | 2404 |
| Извещение об ЭА | `fcsNotificationEF` | `oos:zfcs_notificationEFType` | 991 |
| Проект контракта | `cpContractProject` | `cp:contractProjectType` | 4781 |
| Проект контракта (непубличные данные) | `cpClosedContractProject` | `cp:contractProjectType` | 4922 |
| Контракт / реестр | `contract` | `oos:zfcs_contract2015Type` | 693 |
| Подписание контракта | `fcsContractSign` | `oos:zfcs_contractSignType` | 1445 |
| Исполнение контракта | `contractExecution` | `ce:contractExecutionType` | 5802 |
| Документ о приёмке, большая закупка / ЭА (ЦРК) | `ceContractExecutionDocAcceptance` | `ce:contractExecutionDocAcceptanceType` | 6346 |
| Документ о приёмке, малая закупка | `cemContractExecutionDocAcceptance` | `ce:contractExecutionDocAcceptanceType` | 5945 |
| Привлечение СМП (отчёт 238) | `fcsCustomerReportSmallScaleBusiness` | `oos:zfcs_customerReportSmallScaleBusinessType` | 2093 |
| Изменение контракта | `contractCorrection` | `oos:zfcs_contractCorrection2015Type` | 712 |
| Отмена контракта | `contractCancel` | `oos:zfcs_contractCancel2015Type` | 730 |
| Отмена извещения | `fcsNotificationCancel` | `oos:zfcs_notificationCancelType` | 955 |
| Протокол ЭА (итоговый) | `fcsProtocolEF3` | `oos:zfcs_protocolEF3Type` | 1553 |

### Частая ошибка превью

`<contractProject>` **не является глобальным элементом** — это имя complexType
(`IntegrationCPTypes.xsd:6150`). Правильный корень — `cpContractProject`.
Аналогично `<notificationEF2020>` в превью нет в Альбоме: для извещения ЭА нужен
`fcsNotificationEF`.

Для документа о приёмке **не** подходит корень `contractExecution`: он несёт
`ce:contractExecutionType` (сведения об исполнении в целом), а не
`ce:contractExecutionDocAcceptanceType`. Нужен один из двух корней из таблицы
выше; выбор зафиксирован в разделе «Приёмка» ниже.

## Обязательная оболочка `as2PacketType`

Все корни выше наследуют `as2PacketType` (`fcsIntegration.xsd:6567`). Помимо
`data` пакет обязан содержать `index` со следующими детьми (порядок важен):

### Раскладка пространств имён — проверено валидацией 32 XSD

Корень и `index`/`data` живут в `integration/1`, а тело документа — в своём
пространстве (`TPtypes/1`, `types/1`, `CPtypes/1`, `CETypes/1`). Рабочая схема:
префикс пакета (например `p:`) на корне и у `index`/`data`, а `xmlns`
документа по умолчанию — на корне; тогда дети `data` остаются без префиксов и
попадают в нужное пространство. Запись `<data xmlns="…/TPtypes/1">` ломает
валидацию: `data` обязан остаться в `integration/1`.

`schemeVersion="17.0"` — **атрибут элемента `data`**, а не корня пакета. У корня
атрибутов нет: старая запись `schemeVersion` на `<tenderPlan2020>` была ошибкой
XSD. Пустая подпись `<signature type="CAdES-BES"></signature>` валидна
(`xs:base64Binary` допускает нулевую длину) — фальшивую подпись в превью писать
нельзя, ставится только реальная подпись плагина.

| Путь | Тип / ограничение | Комментарий |
|---|---|---|
| `index/id` | `xs:string`, ровно 36 символов | глобальный идентификатор пакета |
| `index/sender` | `xs:string`, до 200 | отправитель |
| `index/receiver` | `xs:string`, до 200 | получатель |
| `index/createDateTime` | `xs:dateTime` | время формирования |
| `index/objectType` | `base:objectTypeEnum` | код объекта обмена (`TP2020`, `EF`, `CP`, `CT`, … — полный список в XSD:6616–6683) |
| `index/objectId` | `xs:string`, до 36, `minOccurs=0` | реестровый номер |
| `index/indexNum` | `xs:int`, `minOccurs=0` | номер пакета в рамках объекта |
| `index/signature` | `xs:base64Binary` + обязательный атрибут `type` | ЭЦП данных пакета |
| `index/mode` | `indexModeType` | `PROD` или `TEST` |
| `index/testCase` | `xs:int`, `minOccurs=0` | номер тестового случая |

Код `objectType` для наших документов: `TP2020` — план-график, `EF` — извещение
электронного аукциона, `CP` — проект контракта, `CT` — контракт, `CRK` — документы
Целевого реестра контрактов (приёмка), `OZ` — отчёт заказчика. Полный список —
`BaseTypes.xsd:3462–3525` / комментарий в `fcsIntegration.xsd:6621–6683`.

## Приёмка: выбор корня и обязательные справочники (`Б7`)

Маппер приёмки (`Csms.Infrastructure/AcceptanceXmlMapper.cs`) использует
`ceContractExecutionDocAcceptance` с `objectType=CRK`: это документ о приёмке
для большой закупки / электронного аукциона. Корень
`cemContractExecutionDocAcceptance` — тот же тип данных, но для малой закупки;
переключение потребуется отдельным решением, когда в домене появится признак
способы закупки.

Внутри `data` заполняются `docAcceptanceInfo` (тип документа, дата, номер, дата
приёмки, `fulfilmentSumRUR`, позиции с `fulfilledCost`). Значения, которых нет в
домене, **не подбираются по названию**:

- `documentType/code` — код из справочника `nsiContractExecutionDoc`;
- `placer/responsibleOrg/regNum` — код заказчика по СПЗ (11 цифр);
- `VATRateInfo/VATCode` — код из `nsiVAT`;
- `OKPD2Info/OKPDCode`, `originCountry/countryCode` — коды из справочников.

Они передаются явным контекстом маппера. Если обязательного значения нет,
маппер бросает `DomainException` с именем поля вместо «похожего на правильный»
XML. Это же правило распространяется на `placeholder-v0`: настоящий маппер не
должен подменять заглушку.

## Аннулирование контракта (`contractCancel`, часть Б10)

Маппер `Csms.Infrastructure/ContractCancelXmlMapper.cs` собирает корень
`contractCancel` (`fcsIntegration.xsd:730`) с `data` типа
`oos:zfcs_contractCancel2015Type`. `objectType=CT`, обязательные дети `data` —
`regNum` (19 цифр из домена), `cancelDate` (`xs:dateTime`), `documentBase`
(реквизиты документа-основания, до 2000 символов). `currentContractStage=IN`
фиксирует аннулирование. XML проверяется тем же `EisSchemaCatalog`, что и
приёмка; источник — подписанное расторжение из `ContractChange`.

Endpoint `POST /api/acceptances/{id}/eis-package` собирает приёмку, а
`POST /api/contract-changes/{id}/eis-package` — аннулирование. Оба маршрута
создают обычный `EisPackage` с ссылкой на документ-источник (Б4).

## Что остаётся открытым

- Схемы ТФФ по 658/215н и 918 в Альбоме 17.0 отсутствуют. Таблица выше
  описывает доступные глобальные корни, но **не** означает, что доменные
  мапперы готовы: для сведений об исполнении и СМП нужны именно ТФФ.
- Поэтому `PlaceholderXmlBuilder` остаётся честной заглушкой
  `placeholder-v0`, пока не будет написан маппер с реальными полями домена.
  Подменять его «похожим на правильный» XML запрещено правилами репозитория.
