using System.Text;
using System.Text.RegularExpressions;
using Csms.Application;

namespace Csms.Infrastructure.Documents;

/// <summary>
/// Детерминированная сегментация текста документа по нумерации разделов и пунктов.
/// Никакого LLM: каждый непустой символ исходного текста попадает ровно в один
/// сегмент, поэтому coverage ledger можно проверить арифметически.
/// </summary>
public sealed class DocumentSegmenter
{
    private const int MaxSegmentLength = 2000;

    // Римские и арабские номера разделов: "III. Функции...", "Раздел 3. ...".
    private static readonly Regex SectionRegex = new(
        @"^\s*(?:(?:РАЗДЕЛ|РАЗДЕЛ|Раздел|раздел)\s+)?([IVXLCDM]+|\d+)[\.\)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Арабская иерархия: "3.", "3.2.", "3.2.2.", "3.2.2.2.".
    private static readonly Regex NumberedRegex = new(
        @"^\s*(\d+(?:\.\d+)*)[\.\)]\s*(.*)$",
        RegexOptions.Compiled);

    // Буквенные и цифровые подпункты: "а)", "1)", "2.".
    private static readonly Regex SubItemRegex = new(
        @"^\s*([а-яёa-z]|\d+)[\.\)]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed record Heading(string Path, string Text, int Level);

    private sealed record ParsedHeading(string Path, string Text, int Level);

    public IReadOnlyList<DocumentSegment> Segment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var segments = new List<DocumentSegment>();
        var stack = new List<Heading>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new StringBuilder();
        var bufferStart = 0;
        var offset = 0;
        var frontNumber = 0;

        void Flush(int end)
        {
            if (buffer.Length == 0)
                return;

            var body = buffer.ToString().Trim();
            if (body.Length == 0)
            {
                buffer.Clear();
                return;
            }

            var ancestors = stack
                .Select(h => new DocumentSegmentAncestor(h.Path, TrimForContext(h.Text)))
                .ToList();
            var path = ancestors.Count == 0
                ? "Преамбула"
                : string.Join(" → ", ancestors.Select(a => a.Path));
            var baseId = ancestors.Count == 0
                ? $"front-{++frontNumber}"
                : MakeId(ancestors[^1].Path);

            // Буквенные подпункты «а)» повторяются в разных пунктах. Без
            // уточнения ID словарь сегментов упал бы на дубликате.
            var id = baseId;
            var suffix = 2;
            while (!usedIds.Add(id))
                id = baseId + "-" + suffix++;

            segments.AddRange(SplitLongSegment(
                id, path, ancestors, bufferStart, end, body));
            buffer.Clear();
        }

        foreach (var line in lines)
        {
            var heading = ParseHeading(line);
            if (heading is not null)
            {
                Flush(offset);
                while (stack.Count > 0 && stack[^1].Level >= heading.Level)
                    stack.RemoveAt(stack.Count - 1);

                // Разделы верхнего уровня (уровень -1) тоже попадают в стек:
                // без них «хлебные крошки» теряют III. Функции... и т.п.
                stack.Add(new Heading(heading.Path, heading.Text, heading.Level));

                bufferStart = offset;
                buffer.AppendLine(line);
            }
            else
            {
                if (buffer.Length == 0)
                    bufferStart = offset;
                buffer.AppendLine(line);
            }

            offset += line.Length + 1;
        }

        Flush(Math.Max(0, offset - 1));
        return segments;
    }

    private static IEnumerable<DocumentSegment> SplitLongSegment(
        string id,
        string path,
        IReadOnlyList<DocumentSegmentAncestor> ancestors,
        int start,
        int end,
        string body)
    {
        if (body.Length <= MaxSegmentLength)
        {
            return [new DocumentSegment(id, path, ancestors, FindPage(body), start, end, body)];
        }

        var parts = new List<DocumentSegment>();
        var sentences = Regex.Split(body, @"(?<=[.!?])\s+");
        var chunk = new StringBuilder();
        var part = 0;

        foreach (var sentence in sentences)
        {
            if (chunk.Length > 0 && chunk.Length + sentence.Length > MaxSegmentLength)
            {
                part++;
                parts.Add(new DocumentSegment(
                    id + "#" + part, path, ancestors, FindPage(chunk.ToString()),
                    start, end, chunk.ToString().Trim()));
                chunk.Clear();
            }
            if (chunk.Length > 0)
                chunk.Append(' ');
            chunk.Append(sentence.Trim());
        }

        if (chunk.Length > 0)
        {
            part++;
            parts.Add(new DocumentSegment(
                id + "#" + part, path, ancestors, FindPage(chunk.ToString()),
                start, end, chunk.ToString().Trim()));
        }

        return parts;
    }

    private static int? FindPage(string text)
    {
        var match = Regex.Match(text, @"(?:стр\.?|страница)\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var page) ? page : null;
    }

    private static string TrimForContext(string text) =>
        text.Length <= 240 ? text : text[..240].TrimEnd() + "…";

    private static string MakeId(string path)
    {
        var parts = path.Split(" → ", StringSplitOptions.RemoveEmptyEntries);
        var last = parts.Length == 0 ? path : parts[^1];
        var cleaned = Regex.Replace(last.ToLowerInvariant(), @"[^0-9a-zа-яё]+", "-")
            .Trim('-');
        return "p-" + (string.IsNullOrEmpty(cleaned) ? "block" : cleaned);
    }

    private static ParsedHeading? ParseHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var section = SectionRegex.Match(line);
        if (section.Success)
        {
            // Римские номера — раздел верхнего уровня, арабские — тоже раздел.
            return new ParsedHeading(section.Groups[1].Value, line.Trim(), -1);
        }

        var numbered = NumberedRegex.Match(line);
        if (numbered.Success)
        {
            var number = numbered.Groups[1].Value;
            var level = number.Count(c => c == '.') + 1;
            // "3.2." и "3.2.2." — заголовки, но их текст может быть и нормой.
            return new ParsedHeading(number.TrimEnd('.'), line.Trim(), level);
        }

        var sub = SubItemRegex.Match(line);
        if (sub.Success)
            return new ParsedHeading(sub.Groups[1].Value, line.Trim(), 6);

        return null;
    }
}
