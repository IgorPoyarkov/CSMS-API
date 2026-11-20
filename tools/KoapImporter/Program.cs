using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace KoapImporter;

/// <summary>
/// Загрузчик КоАП РФ из веб-версии КонсультантПлюс в схему legal.
/// Разбирает оглавление документа, каждую статью и её части, строит чанки
/// и эмбеддинги локальной моделью Ollama. Повторный запуск идемпотентен.
/// </summary>
internal static class Program
{
    private const string DocumentUrl = "http://www.consultant.ru/document/cons_doc_LAW_34661/";
    private const string ActCode = "koap";
    private const string ActVersion = "koap-2026-08-04";
    private const string DocumentTitle =
        "Кодекс Российской Федерации об административных правонарушениях от 30.12.2001 N 195-ФЗ (ред. от 04.08.2026)";
    private const string EmbeddingModel = "nomic-embed-text";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex TocLinkRegex = new(
        "<li><a href=\"(?<href>[^\"]+)\">(?<title>[^<]+)</a></li>",
        RegexOptions.Compiled);
    private static readonly Regex ArticleNumberRegex = new(@"^Статья\s+(?<number>[\d.]+)\.", RegexOptions.Compiled);
    private static readonly Regex PartRegex = new(@"^(?<number>\d{1,2})\.\s", RegexOptions.Compiled);

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(180) };

            List<TocEntry> toc;
            if (options.LoadOnly && File.Exists(options.SnapshotPath))
            {
                toc = [];
            }
            else
            {
                toc = await ParseTocAsync(http);
                Console.WriteLine($"Оглавление: {toc.Count} статей/разделов");

                var articles = toc.Where(entry => entry.IsArticle).ToArray();
                Console.WriteLine($"Статей для загрузки: {articles.Length}");
                if (options.Limit > 0)
                {
                    Console.WriteLine($"Ограничение: первые {options.Limit} статей");
                }

                var norms = await ParseArticlesAsync(http, articles, options.Limit);
                Console.WriteLine($"Структурных единиц: {norms.Count} " +
                                  $"(частей: {norms.Count(norm => norm.StructuralKind == "part")})");

                var json = JsonSerializer.Serialize(norms, JsonOptions);
                await File.WriteAllTextAsync(options.SnapshotPath, json, new UTF8Encoding(false));
                Console.WriteLine($"Снимок: {options.SnapshotPath}");

                if (!options.ParseOnly)
                {
                    await LoadAsync(norms, options, http);
                }
            }

            if (options.LoadOnly)
            {
                if (!File.Exists(options.SnapshotPath))
                {
                    Console.Error.WriteLine($"Снимок не найден: {options.SnapshotPath}");
                    return 2;
                }

                var norms = JsonSerializer.Deserialize<List<NormDraft>>(
                    await File.ReadAllTextAsync(options.SnapshotPath), JsonOptions) ?? [];
                await LoadAsync(norms, options, http);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка: {exception}");
            return 1;
        }
    }

    private static async Task<List<TocEntry>> ParseTocAsync(HttpClient http)
    {
        var html = await FetchAsync(http, DocumentUrl);
        var start = html.IndexOf("<div class=\"document-page__toc\"", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("Оглавление документа не найдено");
        }

        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        var tocHtml = html[start..end];

        var result = new List<TocEntry>();
        string section = string.Empty;
        string chapter = string.Empty;
        foreach (Match match in TocLinkRegex.Matches(tocHtml))
        {
            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            var href = match.Groups["href"].Value;
            if (title.StartsWith("Раздел ", StringComparison.Ordinal))
            {
                section = title;
                continue;
            }

            if (title.StartsWith("Глава ", StringComparison.Ordinal))
            {
                chapter = title;
                continue;
            }

            if (!title.StartsWith("Статья ", StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new TocEntry(
                new Uri(new Uri(DocumentUrl), href).ToString(),
                title,
                section,
                chapter));
        }

        return result;
    }

    private static async Task<List<NormDraft>> ParseArticlesAsync(
        HttpClient http, IReadOnlyList<TocEntry> articles, int limit)
    {
        var selected = limit > 0 ? articles.Take(limit).ToArray() : articles.ToArray();
        var norms = new List<NormDraft>();
        var semaphore = new SemaphoreSlim(4);
        var tasks = selected.Select(async entry =>
        {
            await semaphore.WaitAsync();
            try
            {
                var html = await FetchAsync(http, entry.Url);
                return entry.Title.Contains("Утратил", StringComparison.OrdinalIgnoreCase)
                    ? ParseExpiredArticle(entry, html)
                    : ParseArticle(entry, html);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var arrays = await Task.WhenAll(tasks);
        foreach (var array in arrays)
        {
            norms.AddRange(array);
        }

        return norms
            .GroupBy(norm => norm.LiabilityNodeId)
            .Select(group => group.First())
            .OrderBy(norm => norm.LiabilityNodeId, StringComparer.Ordinal)
            .ToList();
    }

    private static List<NormDraft> ParseArticle(TocEntry entry, string html)
    {
        var numberMatch = ArticleNumberRegex.Match(entry.Title);
        var articleNumber = numberMatch.Success
            ? numberMatch.Groups["number"].Value.TrimEnd('.')
            : "unknown";

        var contentStart = html.IndexOf("document-page__content", StringComparison.Ordinal);
        if (contentStart < 0)
        {
            return [];
        }

        // После основного текста идут блок «страницы» и повтор заголовка статьи.
        // Их нужно отрезать, иначе они попадут в текст последней части нормы.
        var contentEnd = new[]
            {
                html.IndexOf("document-page__footer", contentStart, StringComparison.Ordinal),
                html.IndexOf("document-page__separator", contentStart, StringComparison.Ordinal),
                html.IndexOf("class=\"pages\"", contentStart, StringComparison.Ordinal),
            }
            .Where(index => index > contentStart)
            .DefaultIfEmpty(html.Length)
            .Min();
        var content = html[contentStart..contentEnd];
        var paragraphs = new List<string>();
        foreach (Match match in Regex.Matches(content, "<p[^>]*>(?<body>.*?)</p>",
                     RegexOptions.Singleline | RegexOptions.Compiled))
        {
            var text = Normalize(WebUtility.HtmlDecode(
                Regex.Replace(match.Groups["body"].Value, "<[^>]+>", " ")));
            if (text.Length == 0 || text.StartsWith("КоАП РФ Статья", StringComparison.Ordinal))
            {
                continue;
            }

            if (IsRequisition(text))
            {
                continue;
            }

            paragraphs.Add(text);
        }

        if (paragraphs.Count == 0)
        {
            return ParseExpiredArticle(entry, html);
        }

        var parts = new List<ArticlePart>();
        ArticlePart? current = null;
        foreach (var text in paragraphs)
        {
            var partMatch = PartRegex.Match(text);
            if (partMatch.Success)
            {
                current = new ArticlePart(partMatch.Groups["number"].Value);
                parts.Add(current);
            }
            else
            {
                current ??= new ArticlePart(null);
            }

            current.Paragraphs.Add(text);
        }

        var path = new[] { entry.Section, entry.Chapter, entry.Title }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Select(part =>
        {
            var partSuffix = part.Number is null ? string.Empty : $":p{part.Number}";
            var nodeId = $"{ActCode}:195fz:{articleNumber}{partSuffix}";
            var reference = part.Number is null
                ? $"КоАП РФ ст. {articleNumber}"
                : $"КоАП РФ ст. {articleNumber} ч. {part.Number}";
            var text = string.Join(" ", part.Paragraphs);
            var metadata = new Dictionary<string, string>
            {
                ["source"] = "consultant-web",
                ["sourceUrl"] = entry.Url,
                ["documentEdition"] = "ред. от 04.08.2026",
                ["effectiveFrom"] = "2026-11-03",
                ["article"] = articleNumber,
                ["part"] = part.Number ?? string.Empty,
                ["section"] = entry.Section,
                ["chapter"] = entry.Chapter,
                ["paragraphCount"] = part.Paragraphs.Count.ToString(CultureInfo.InvariantCulture),
            };

            return new NormDraft(
                nodeId,
                reference,
                entry.Title,
                text,
                part.Number is null ? "article" : "part",
                path,
                ClassifyNormType(articleNumber, entry.Section),
                "in_force",
                metadata);
        }).ToList();
    }

    private static List<NormDraft> ParseExpiredArticle(TocEntry entry, string html)
    {
        var numberMatch = ArticleNumberRegex.Match(entry.Title);
        var articleNumber = numberMatch.Success
            ? numberMatch.Groups["number"].Value.TrimEnd('.')
            : "unknown";
        var nodeId = $"{ActCode}:195fz:{articleNumber}";
        var text = entry.Title;
        return
        [
            new NormDraft(
                nodeId,
                $"КоАП РФ ст. {articleNumber}",
                entry.Title,
                text,
                "article",
                [entry.Section, entry.Chapter, entry.Title],
                ClassifyNormType(articleNumber, entry.Section),
                "expired",
                new Dictionary<string, string>
                {
                    ["source"] = "consultant-web",
                    ["sourceUrl"] = entry.Url,
                    ["documentEdition"] = "ред. от 04.08.2026",
                    ["article"] = articleNumber,
                })
        ];
    }

    private static bool IsRequisition(string text) =>
        text.StartsWith("(введена", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("(в ред.", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("(утратила", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("(изменена", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("(дополнена", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyNormType(string articleNumber, string section)
    {
        if (articleNumber.StartsWith("23.", StringComparison.Ordinal) ||
            articleNumber.StartsWith("24.", StringComparison.Ordinal))
        {
            return "procedural";
        }

        if (articleNumber is "2.4" or "2.9" or "2.9.1" or "4.1" or "4.1.1" or "4.1.2")
        {
            return "blanket";
        }

        var firstPart = articleNumber.Split('.')[0];
        if (int.TryParse(firstPart, out var chapter) && chapter <= 4)
        {
            return "general";
        }

        return "special";
    }

    private static async Task LoadAsync(
        List<NormDraft> norms, Options options, HttpClient http)
    {
        Console.WriteLine($"Подготовка эмбеддингов: {norms.Count} норм");
        var allChunks = new List<(NormDraft Norm, int Index, string Text)>();
        foreach (var norm in norms)
        {
            var chunks = BuildChunks(norm.Text, 1400, 150);
            for (var index = 0; index < chunks.Count; index++)
            {
                allChunks.Add((norm, index, chunks[index]));
            }
        }
        Console.WriteLine($"Чанков: {allChunks.Count}");

        if (!options.ParseOnly)
        {
            var embeddings = await EmbedAllAsync(http, allChunks.Select(item => item.Text).ToArray());
            await using var connection = new NpgsqlConnection(options.ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            for (var index = 0; index < norms.Count; index++)
            {
                var norm = norms[index];
                var normId = await UpsertNormAsync(connection, transaction, norm);
                var chunkCount = allChunks.Count(item => ReferenceEquals(item.Norm, norm));

                await using (var delete = new NpgsqlCommand(
                    "DELETE FROM legal.liability_norm_chunks WHERE norm_id = @id", connection, transaction))
                {
                    delete.Parameters.AddWithValue("id", normId);
                    await delete.ExecuteNonQueryAsync();
                }

                var firstChunk = allChunks.FindIndex(item => ReferenceEquals(item.Norm, norm));
                for (var offset = 0; offset < chunkCount; offset++)
                {
                    var chunk = allChunks[firstChunk + offset];
                    await UpsertChunkAsync(
                        connection, transaction, normId, chunk.Index, chunk.Text, embeddings[firstChunk + offset]);
                }

                if ((index + 1) % 50 == 0)
                {
                    Console.WriteLine($"Загружено {index + 1}/{norms.Count}");
                }
            }

            await transaction.CommitAsync();
            Console.WriteLine("Загрузка в legal.liability_norms завершена");
        }
    }

    private static async Task<Guid> UpsertNormAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, NormDraft norm)
    {
        var id = DeterministicGuid(norm.LiabilityNodeId);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO legal.liability_norms
                (id, liability_node_id, act_code, act_version, document_title, structural_kind,
                 structural_path, norm_reference, heading, text, norm_type, status, edition_date,
                 metadata, source_url, created_at, updated_at)
            VALUES
                (@id, @nodeId, @actCode, @actVersion, @documentTitle, @structuralKind,
                 CAST(@path AS jsonb), @reference, @heading, @text, @normType, @status, @editionDate,
                 CAST(@metadata AS jsonb), @sourceUrl, now(), now())
            ON CONFLICT (act_code, act_version, liability_node_id) DO UPDATE SET
                document_title = EXCLUDED.document_title,
                structural_kind = EXCLUDED.structural_kind,
                structural_path = EXCLUDED.structural_path,
                norm_reference = EXCLUDED.norm_reference,
                heading = EXCLUDED.heading,
                text = EXCLUDED.text,
                norm_type = EXCLUDED.norm_type,
                status = EXCLUDED.status,
                edition_date = EXCLUDED.edition_date,
                metadata = EXCLUDED.metadata,
                source_url = EXCLUDED.source_url,
                updated_at = now()
            RETURNING id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("nodeId", norm.LiabilityNodeId);
        command.Parameters.AddWithValue("actCode", ActCode);
        command.Parameters.AddWithValue("actVersion", ActVersion);
        command.Parameters.AddWithValue("documentTitle", DocumentTitle);
        command.Parameters.AddWithValue("structuralKind", norm.StructuralKind);
        command.Parameters.AddWithValue("path", JsonSerializer.Serialize(norm.StructuralPath, JsonOptions));
        command.Parameters.AddWithValue("reference", norm.NormReference);
        command.Parameters.AddWithValue("heading", norm.Heading);
        command.Parameters.AddWithValue("text", norm.Text);
        command.Parameters.AddWithValue("normType", norm.NormType);
        command.Parameters.AddWithValue("status", norm.Status);
        command.Parameters.AddWithValue("editionDate", new DateOnly(2026, 8, 4));
        command.Parameters.AddWithValue(
            "metadata", JsonSerializer.Serialize(norm.Metadata, JsonOptions));
        command.Parameters.AddWithValue(
            "sourceUrl", norm.Metadata.GetValueOrDefault("sourceUrl") ?? DocumentUrl);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task UpsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid normId,
        int index,
        string text,
        float[] embedding)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO legal.liability_norm_chunks
                (id, norm_id, chunk_index, chunk_text, embedding, token_count, created_at)
            VALUES
                (@id, @normId, @index, @text, CAST(@embedding AS vector(768)), @tokens, now())
            ON CONFLICT (norm_id, chunk_index) DO UPDATE SET
                chunk_text = EXCLUDED.chunk_text,
                embedding = EXCLUDED.embedding,
                token_count = EXCLUDED.token_count
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("normId", normId);
        command.Parameters.AddWithValue("index", index);
        command.Parameters.AddWithValue("text", text);
        command.Parameters.AddWithValue("embedding", ToPgVector(embedding));
        command.Parameters.AddWithValue("tokens", text.Length / 4);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<float[][]> EmbedAllAsync(HttpClient http, string[] texts)
    {
        var result = new float[texts.Length][];
        const int batchSize = 16;
        for (var start = 0; start < texts.Length; start += batchSize)
        {
            var batch = texts[start..Math.Min(start + batchSize, texts.Length)];
            var body = JsonSerializer.Serialize(new { model = EmbeddingModel, input = batch }, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:11434/api/embed")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var embeddings = document.RootElement.GetProperty("embeddings");
            var index = 0;
            foreach (var embedding in embeddings.EnumerateArray())
            {
                result[start + index] = embedding.EnumerateArray()
                    .Select(value => value.GetSingle())
                    .ToArray();
                index++;
            }

            if (index != batch.Length)
            {
                throw new InvalidOperationException($"Ollama вернул {index} эмбеддингов вместо {batch.Length}");
            }

            if (result[start].Length != 768)
            {
                throw new InvalidOperationException(
                    $"Размерность эмбеддинга {result[start].Length}, ожидалось 768");
            }

            Console.WriteLine($"Эмбеддинги: {Math.Min(start + batchSize, texts.Length)}/{texts.Length}");
        }

        return result;
    }

    private static List<string> BuildChunks(string text, int maxLength, int overlap)
    {
        if (text.Length <= maxLength)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(start + maxLength, text.Length);
            if (end < text.Length)
            {
                var boundary = text.LastIndexOf(' ', end);
                if (boundary > start + maxLength / 2)
                {
                    end = boundary;
                }
            }

            chunks.Add(text[start..end].Trim());
            if (end >= text.Length)
            {
                break;
            }

            start = Math.Max(start + 1, end - overlap);
        }

        return chunks;
    }

    private static async Task<string> FetchAsync(HttpClient http, string url)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(url);
                return Encoding.UTF8.GetString(bytes);
            }
            catch when (attempt < 3)
            {
                await Task.Delay(500 * attempt);
            }
        }

        throw new InvalidOperationException($"Не удалось загрузить {url}");
    }

    private static string Normalize(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string ToPgVector(float[] vector) =>
        "[" + string.Join(",", vector.Select(value => value.ToString("G9", CultureInfo.InvariantCulture))) + "]";

    private sealed record TocEntry(string Url, string Title, string Section, string Chapter)
    {
        public bool IsArticle => Title.StartsWith("Статья ", StringComparison.Ordinal);
    }

    private sealed record NormDraft(
        string LiabilityNodeId,
        string NormReference,
        string Heading,
        string Text,
        string StructuralKind,
        string[] StructuralPath,
        string NormType,
        string Status,
        Dictionary<string, string> Metadata);

    private sealed class ArticlePart(string? number)
    {
        public string? Number { get; } = number;
        public List<string> Paragraphs { get; } = [];
    }

    private sealed class Options
    {
        public string ConnectionString { get; private init; } =
            "Host=localhost;Port=5433;Database=csms_eis;Username=postgres";

        public string SnapshotPath { get; private init; } =
            Path.Combine(Path.GetTempPath(), "opencode", "koap-norms.json");

        public bool ParseOnly { get; private init; }
        public bool LoadOnly { get; private init; }
        public int Limit { get; private init; }

        public static Options Parse(string[] args)
        {
            var connection = Environment.GetEnvironmentVariable("CSMS_DB_CONNECTION");
            var snapshot = OptionsDefaultSnapshot();
            var parseOnly = false;
            var loadOnly = false;
            var limit = 0;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--connection" when index + 1 < args.Length:
                        connection = args[++index];
                        break;
                    case "--snapshot" when index + 1 < args.Length:
                        snapshot = args[++index];
                        break;
                    case "--limit" when index + 1 < args.Length:
                        limit = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    case "--parse-only":
                        parseOnly = true;
                        break;
                    case "--load-only":
                        loadOnly = true;
                        break;
                    default:
                        throw new ArgumentException($"Неизвестный аргумент: {args[index]}");
                }
            }

            return new Options
            {
                ConnectionString = string.IsNullOrWhiteSpace(connection)
                    ? "Host=localhost;Port=5433;Database=csms_eis;Username=postgres"
                    : connection,
                SnapshotPath = snapshot,
                ParseOnly = parseOnly,
                LoadOnly = loadOnly,
                Limit = limit,
            };
        }

        private static string OptionsDefaultSnapshot() =>
            Path.Combine(Path.GetTempPath(), "opencode", "koap-norms.json");
    }
}
