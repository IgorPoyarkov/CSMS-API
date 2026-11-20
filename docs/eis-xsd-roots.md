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

## Обязательная оболочка `as2PacketType`

Все корни выше наследуют `as2PacketType` (`fcsIntegration.xsd:6567`). Помимо
`data` пакет обязан содержать `index` со следующими детьми (порядок важен):

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
электронного аукциона, `CP` — проект контракта, `CT` — контракт, `OZ` — отчёт
заказчика.

## Что остаётся открытым

- Схемы ТФФ по 658/215н и 918 в Альбоме 17.0 отсутствуют. Таблица выше
  описывает доступные глобальные корни, но **не** означает, что доменные
  мапперы готовы: для сведений об исполнении и СМП нужны именно ТФФ.
- Поэтому `PlaceholderXmlBuilder` остаётся честной заглушкой
  `placeholder-v0`, пока не будет написан маппер с реальными полями домена.
  Подменять его «похожим на правильный» XML запрещено правилами репозитория.
