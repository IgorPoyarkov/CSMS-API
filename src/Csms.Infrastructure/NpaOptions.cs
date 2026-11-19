namespace Csms.Infrastructure;

/// <summary>
/// Настройки агента, который размечает коды ОКПД2 по официальным актам.
///
/// Источник текста — официальный портал опубликования правовых актов
/// (publication.pravo.gov.ru). PDF там — сканы без текстового слоя, поэтому
/// страницы рендерятся в растр и распознаются Tesseract с русским языком.
/// Настройки лежат в appsettings.json (раздел Npa) и переопределяются
/// переменными окружения Npa__*.
/// </summary>
public sealed class NpaOptions
{
    public const string SectionName = "Npa";

    /// <summary>Фоновая проверка новых публикаций.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Как часто проверять, не изменился ли официальный текст.</summary>
    public int SyncIntervalHours { get; set; } = 24;

    /// <summary>
    /// Публиковать новую версию правил сразу. Если false — версия готовится,
    /// но активной остаётся прежняя (решение принимает человек).
    /// </summary>
    public bool AutoPromote { get; set; } = true;

    /// <summary>
    /// Минимальная уверенность, при которой правило применяется автоматически.
    /// Ниже порога правило сохраняется со статусом needs_review.
    /// </summary>
    public double MinAutoConfidence { get; set; } = 0.8;

    /// <summary>Сколько строк правил отдавать за один запрос по умолчанию.</summary>
    public int DefaultPageSize { get; set; } = 200;

    public NpaOcrOptions Ocr { get; set; } = new();

    public NpaLlmOptions Llm { get; set; } = new();

    /// <summary>Акты, которые агент ведёт. Порядок не важен.</summary>
    public List<NpaDocumentOptions> Documents { get; set; } = [];

    public void Validate()
    {
        if (SyncIntervalHours < 1)
        {
            throw new InvalidOperationException("Npa:SyncIntervalHours должен быть не меньше 1.");
        }

        if (MinAutoConfidence is < 0 or > 1)
        {
            throw new InvalidOperationException("Npa:MinAutoConfidence должен быть в диапазоне 0..1.");
        }

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in Documents)
        {
            if (string.IsNullOrWhiteSpace(document.Code))
            {
                throw new InvalidOperationException("Npa:Documents: у акта не задан Code.");
            }

            if (!unique.Add(document.Code))
            {
                throw new InvalidOperationException(
                    $"Npa:Documents: код акта {document.Code} указан дважды.");
            }

            if (string.IsNullOrWhiteSpace(document.EoNumber) &&
                string.IsNullOrWhiteSpace(document.File))
            {
                throw new InvalidOperationException(
                    $"Npa:Documents:{document.Code}: нужен EoNumber (публикация на портале) либо File (локальный файл).");
            }
        }

        Ocr.Validate();
        Llm.Validate();
    }

    /// <summary>Находит настройки акта по коду.</summary>
    public NpaDocumentOptions? Find(string code) =>
        Documents.FirstOrDefault(document =>
            document.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Параметры распознавания сканов.</summary>
public sealed class NpaOcrOptions
{
    public string? TesseractPath { get; set; }

    /// <summary>Каталог с rus.traineddata и configs/tsv.</summary>
    public string? TessdataPath { get; set; }

    public string Language { get; set; } = "rus";

    /// <summary>Разрешение рендера страницы. 300 DPI — компромисс скорости и качества.</summary>
    public int Dpi { get; set; } = 300;

    /// <summary>Сколько страниц распознавать одновременно.</summary>
    public int ParallelPages { get; set; } = 2;

    /// <summary>Предел страниц в одном акте: защита от «случайно 900 страниц».</summary>
    public int MaxPages { get; set; } = 400;

    public void Validate()
    {
        if (Dpi is < 150 or > 600)
        {
            throw new InvalidOperationException("Npa:Ocr:Dpi должен быть в диапазоне 150..600.");
        }

        if (ParallelPages is < 1 or > 8)
        {
            throw new InvalidOperationException("Npa:Ocr:ParallelPages должен быть в диапазоне 1..8.");
        }
    }
}

/// <summary>
/// Локальная модель в контуре (Ollama). Используется только для спорных
/// строк: код не найден в справочнике или условие прочитано неоднозначно.
/// Результат всегда попадает в needs_review, а не применяется молча.
/// </summary>
public sealed class NpaLlmOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen2.5-coder:7b";

    /// <summary>Таймаут одного запроса к модели.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Сколько строк отправлять модели за один заход.</summary>
    public int BatchSize { get; set; } = 10;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Npa:Llm:Endpoint должен быть абсолютным адресом.");
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException("Npa:Llm:Model не задан.");
        }
    }
}

/// <summary>Один акт, который ведёт агент.</summary>
public sealed class NpaDocumentOptions
{
    /// <summary>Код акта: rp-471, pp-1875, pp-2604.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Заголовок акта для интерфейса.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Номер акта: 471-р, 1875, 2604.</summary>
    public string? Number { get; set; }

    /// <summary>Идентификатор официального опубликования (eoNumber).</summary>
    public string? EoNumber { get; set; }

    /// <summary>Блок портала: government — постановления и распоряжения Правительства.</summary>
    public string Block { get; set; } = "government";

    /// <summary>Локальный файл (PDF) вместо портала: когда нужна конкретная редакция.</summary>
    public string? File { get; set; }

    /// <summary>
    /// Левая граница таблицы в долях ширины страницы: левее — поля и колонтитулы.
    /// Правую границу колонки кодов парсер определяет сам по странице.
    /// </summary>
    public double CodeColumnStart { get; set; } = 0.06;

    /// <summary>Распознавать таблицу построчно (колонка кодов + наименование).</summary>
    public bool TableLayout { get; set; } = true;
}
