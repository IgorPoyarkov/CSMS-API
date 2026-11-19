using System.Text;

namespace Csms.Infrastructure.Npa;

/// <summary>Строка таблицы акта: код ОКПД2 и наименование из соседней колонки.</summary>
public sealed record NpaTableRow(
    int Page,
    int RowIndex,
    string Code,
    string? Name,
    string? Quote,
    double CodeConfidence,
    string CodeSource);

/// <summary>Код, который акт выводит из перечня («кроме кодов 01.4, 01.7»).</summary>
public sealed record NpaExclusion(int Page, string Code, string Quote);

/// <summary>Строка, которую агент не смог превратить в код: уходит человеку.</summary>
public sealed record NpaUnresolvedRow(int Page, int RowIndex, string Text, string Reason);

/// <summary>Результат разбора страницы.</summary>
public sealed record NpaPageResult(
    int Page,
    IReadOnlyList<NpaTableRow> Rows,
    IReadOnlyList<NpaExclusion> Exclusions,
    IReadOnlyList<NpaUnresolvedRow> Unresolved);

/// <summary>
/// Восстанавливает таблицу акта из двух проекций OCR. Схема:
/// колонка кодов берётся из «цифровой» проекции (там нет путаницы 0/О),
/// наименование — из обычной, границы строк — по вертикальным центрам кодов.
/// Граница колонки наименований не задаётся константой, а вычисляется по
/// странице: у разных актов таблица свёрстана по-разному.
/// </summary>
public sealed class NpaLayoutParser
{
    private static readonly string[] ExclusionMarkers =
    [
        "кроме", "исключ", "за исключением", "не применя", "не распространя",
    ];

    public NpaPageResult Parse(OcrPage page, NpaDocumentOptions document, Okpd2CodeResolver resolver)
    {
        var lines = BuildLines(page.Text);
        var top = page.Height * 0.05;
        var bottom = page.Height * 0.95;
        var leftLimit = page.Width * document.CodeColumnStart;

        var candidates = new List<(OcrWord Word, CodeResolution Resolution)>();
        foreach (var word in page.Numeric)
        {
            if (word.CenterY < top || word.CenterY > bottom || word.Left < leftLimit ||
                word.Confidence < 40)
            {
                continue;
            }

            var resolution = resolver.Resolve(word.Text);
            if (resolution is not null)
            {
                candidates.Add((word, resolution));
            }
        }

        if (candidates.Count == 0)
        {
            return new NpaPageResult(page.PageNumber, [], [], []);
        }

        var rows = new List<NpaTableRow>();
        var exclusions = new List<NpaExclusion>();
        var unresolved = new List<NpaUnresolvedRow>();

        if (!document.TableLayout)
        {
            CollectExclusions(candidates, lines, page, exclusions);
            return new NpaPageResult(page.PageNumber, rows, exclusions, unresolved);
        }

        // Колонка кодов = самая левая группа чисел на странице.
        var medianLeft = Median(candidates.Select(item => item.Word.Left).ToList());
        var columnTolerance = (int)(page.Width * 0.10);
        var codeColumn = candidates
            .Where(item => item.Word.Left <= medianLeft + columnTolerance)
            .OrderBy(item => item.Word.CenterY)
            .ToList();

        var otherNumbers = candidates.Except(codeColumn).ToList();

        if (codeColumn.Count == 0)
        {
            CollectExclusions(otherNumbers, lines, page, exclusions);
            return new NpaPageResult(page.PageNumber, [], exclusions, unresolved);
        }

        var nameLeft = ResolveNameColumnLeft(codeColumn, lines);
        var lineHeight = EstimateLineHeight(lines);

        for (var index = 0; index < codeColumn.Count; index++)
        {
            var (word, resolution) = codeColumn[index];
            var upper = index == 0
                ? word.CenterY - lineHeight
                : (codeColumn[index - 1].Word.CenterY + word.CenterY) / 2;
            var lower = index == codeColumn.Count - 1
                ? word.CenterY + lineHeight
                : (word.CenterY + codeColumn[index + 1].Word.CenterY) / 2;

            var name = JoinLines(lines, nameLeft, upper, lower);
            rows.Add(new NpaTableRow(
                page.PageNumber,
                index + 1,
                resolution.Code,
                name,
                name,
                resolution.Confidence,
                resolution.Method));
        }

        CollectExclusions(otherNumbers, lines, page, exclusions);
        CollectUnresolved(lines, nameLeft, codeColumn, rows, page, unresolved);

        return new NpaPageResult(page.PageNumber, rows, exclusions, unresolved);
    }

    /// <summary>
    /// Левая граница колонки наименований: самое левое слово справа от кода
    /// на тех строках, где код уже найден.
    /// </summary>
    private static int ResolveNameColumnLeft(
        IReadOnlyList<(OcrWord Word, CodeResolution Resolution)> codeColumn,
        IReadOnlyList<Line> lines)
    {
        var left = int.MaxValue;

        foreach (var (word, _) in codeColumn)
        {
            var line = NearestLine(lines, word.CenterY);
            if (line is null)
            {
                continue;
            }

            foreach (var candidate in line.Words)
            {
                if (candidate.Left > word.Right + 4 && candidate.Left < left)
                {
                    left = candidate.Left;
                    break;
                }
            }
        }

        return left == int.MaxValue ? 0 : left;
    }

    private static string? JoinLines(IReadOnlyList<Line> lines, int nameLeft, int upper, int lower)
    {
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            if (line.CenterY <= upper || line.CenterY >= lower)
            {
                continue;
            }

            var parts = line.Words
                .Where(word => word.Left >= nameLeft - 8)
                .OrderBy(word => word.Left)
                .Select(word => word.Text)
                .ToArray();

            if (parts.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(string.Join(' ', parts));
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// Числа справа от колонки кодов — это коды из оговорки «кроме ...».
    /// Берём их только из строк, где есть явный маркер исключения.
    /// </summary>
    private static void CollectExclusions(
        IReadOnlyList<(OcrWord Word, CodeResolution Resolution)> numbers,
        IReadOnlyList<Line> lines,
        OcrPage page,
        List<NpaExclusion> target)
    {
        foreach (var (word, resolution) in numbers)
        {
            var line = NearestLine(lines, word.CenterY);
            if (line is null)
            {
                continue;
            }

            var text = line.Text;
            var isExclusion = ExclusionMarkers.Any(marker =>
                text.Contains(marker, StringComparison.OrdinalIgnoreCase));

            if (!isExclusion)
            {
                continue;
            }

            if (!target.Any(item => item.Code == resolution.Code && item.Page == page.PageNumber))
            {
                target.Add(new NpaExclusion(page.PageNumber, resolution.Code, text));
            }
        }
    }

    /// <summary>
    /// Текст таблицы выше первого кода и вне найденных строк — это строки,
    /// которые агент не разобрал: заголовки, разорванные ячейки, сноски.
    /// </summary>
    private static void CollectUnresolved(
        IReadOnlyList<Line> lines,
        int nameLeft,
        IReadOnlyList<(OcrWord Word, CodeResolution Resolution)> codeColumn,
        IReadOnlyList<NpaTableRow> rows,
        OcrPage page,
        List<NpaUnresolvedRow> target)
    {
        var firstCenter = codeColumn.Min(item => item.Word.CenterY);
        var lastCenter = codeColumn.Max(item => item.Word.CenterY);
        var lineHeight = EstimateLineHeight(lines);

        var index = 0;
        foreach (var line in lines)
        {
            if (line.CenterY <= firstCenter - (lineHeight * 2) ||
                line.CenterY >= lastCenter + (lineHeight * 2))
            {
                continue;
            }

            var words = line.Words.Where(word => word.Left >= nameLeft - 8).ToArray();
            if (words.Length == 0)
            {
                continue;
            }

            // Строка уже попала в чью-то ячейку — значит, разобрана.
            var covered = rows.Any(row =>
                row.Page == page.PageNumber &&
                row.Name is not null &&
                row.Name.Contains(string.Join(' ', words.Select(word => word.Text)), StringComparison.Ordinal));

            if (covered)
            {
                continue;
            }

            index++;
            target.Add(new NpaUnresolvedRow(
                page.PageNumber,
                index,
                string.Join(' ', words.Select(word => word.Text)),
                "нет кода ОКПД2 в колонке"));
        }
    }

    private static Line? NearestLine(IReadOnlyList<Line> lines, int centerY)
    {
        Line? best = null;
        var bestDistance = int.MaxValue;

        foreach (var line in lines)
        {
            var distance = Math.Abs(line.CenterY - centerY);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = line;
            }
        }

        return bestDistance <= 60 ? best : null;
    }

    private static int EstimateLineHeight(IReadOnlyList<Line> lines)
    {
        if (lines.Count == 0)
        {
            return 40;
        }

        var heights = lines.Select(line => line.Height).OrderBy(value => value).ToList();
        return Math.Max(20, heights[heights.Count / 2] * 2);
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        // Для чётного числа берём середину двух центральных значений: иначе
        // верхняя медиана отнесла бы коды исключений к колонке кодов.
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    internal static List<Line> BuildLines(IReadOnlyList<OcrWord> words)
    {
        var ordered = words
            .Where(word => !string.IsNullOrWhiteSpace(word.Text))
            .OrderBy(word => word.CenterY)
            .ThenBy(word => word.Left)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var heights = ordered.Select(word => word.Height).OrderBy(value => value).ToList();
        var tolerance = Math.Max(6, (int)(heights[heights.Count / 2] * 0.55));

        var lines = new List<Line>();
        foreach (var word in ordered)
        {
            Line? target = null;
            var bestDistance = int.MaxValue;

            foreach (var line in lines)
            {
                var distance = Math.Abs(line.CenterY - word.CenterY);
                if (distance <= tolerance && distance < bestDistance)
                {
                    bestDistance = distance;
                    target = line;
                }
            }

            if (target is null)
            {
                lines.Add(new Line(word));
            }
            else
            {
                target.Add(word);
            }
        }

        return lines.OrderBy(line => line.CenterY).ToList();
    }

    /// <summary>Строка текста: слова, отсортированные по X.</summary>
    internal sealed class Line(OcrWord word)
    {
        private readonly List<OcrWord> _words = [word];

        public IReadOnlyList<OcrWord> Words => _words;

        public int CenterY => (int)_words.Average(item => item.CenterY);

        public int Height => (int)_words.Average(item => item.Height);

        public int Left => _words.Min(item => item.Left);

        public string Text => string.Join(
            ' ',
            _words.OrderBy(item => item.Left).Select(item => item.Text));

        public void Add(OcrWord word) => _words.Add(word);
    }
}
