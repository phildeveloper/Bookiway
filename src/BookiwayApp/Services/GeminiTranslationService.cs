using System.Net;
using System.Text;
using System.Text.Json;

namespace BookiwayApp.Services;

public sealed class GeminiTranslationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiTranslationService> _logger;
    private readonly string _apiKey;

    private const string MODEL_NAME = "gemini-2.5-flash";
    public const string DefaultPrompt = """
Выполни параллельный перевод текста в формате таблицы Markdown.
В 1-й колонке должен быть Оригинальный текст на английском языке.
Во 2-й колонке должен быть Перевод на русский язык.

Ключевые требования:
...
Сохранение Структуры:
Не пропускай ни одного предложения, даже короткого.
Сохраняй исходный порядок и структуру абзавцв.
Не объединяй и не разделяй предложения. Каждое предложение оригинала должно соответствовать одной строке перевода.
**КРАЙНЕ ВАЖНО: Никогда не прерывай генерацию посередине предложения или абзаца. Всегда заканчивай перевод. Не останавливай генерацию по причине "MAX_TOKENS" или "LENGTH".**
**В конце ответа добавь маркер завершения: [END_OF_TRANSLATION].**
""";

    public GeminiTranslationService(IHttpClientFactory httpClientFactory, ILogger<GeminiTranslationService> logger, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    }

    public async Task<int> TranslateRangeAsync(string imagesDirectory, int startPage, int endPage, string htmlOutputDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagesDirectory) || !Directory.Exists(imagesDirectory))
        {
            throw new DirectoryNotFoundException($"Images directory '{imagesDirectory}' not found.");
        }

        if (string.IsNullOrWhiteSpace(htmlOutputDirectory))
        {
            throw new ArgumentException("HTML output directory is required.", nameof(htmlOutputDirectory));
        }

        if (startPage < 1 || endPage < startPage)
        {
            throw new ArgumentException("Invalid page range.");
        }

        Directory.CreateDirectory(htmlOutputDirectory);
        Directory.CreateDirectory(Path.Combine(htmlOutputDirectory, "imgs"));

        var allPages = new List<(string Path, int Page)>();
        foreach (var path in Directory.GetFiles(imagesDirectory, "page-*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path); // page-001
            var parts = name.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[1], out var pageNum))
            {
                allPages.Add((path, pageNum));
            }
        }

        if (allPages.Count == 0)
        {
            return 0;
        }

        var files = allPages
            .Where(entry => entry.Page >= startPage && entry.Page <= endPage)
            .OrderBy(entry => entry.Page)
            .ToList();

        var totalDocumentPages = allPages.Max(entry => entry.Page);

        var selectionCount = files.Count;
        if (selectionCount == 0)
        {
            return 0;
        }

        var effectivePrompt = DefaultPrompt;

        for (int i = 0; i < selectionCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagePath = files[i].Path;
            var pageIndexInSelection = i + 1;
            var pageNumber = files[i].Page;

            var translation = await TranslatePageWithGuaranteeAsync(imagePath, effectivePrompt, cancellationToken);
            var relativeImagePath = await CopyImageForStaticHostingAsync(imagePath, htmlOutputDirectory, cancellationToken);

            await CreateHtmlPageAsync(
                translation,
                pageNumber,
                totalDocumentPages,
                htmlOutputDirectory,
                Path.GetFileName(imagePath),
                relativeImagePath,
                cancellationToken);

            progress?.Report((double)pageIndexInSelection / selectionCount);

            if (pageIndexInSelection < selectionCount)
            {
                await Task.Delay(2000, cancellationToken);
            }
        }

        await CreateIndexHtmlAsync(htmlOutputDirectory, cancellationToken);
        return selectionCount;
    }

    private async Task<string> TranslatePageWithGuaranteeAsync(string imagePath, string prompt, CancellationToken cancellationToken)
    {
        const int MaxPageAttempts = 4;
        GeminiTranslationResult? lastAttempt = null;

        for (var attempt = 1; attempt <= MaxPageAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lastAttempt = await GetGeminiTranslationAsync(imagePath, prompt, cancellationToken);
            if (lastAttempt.Success)
            {
                return lastAttempt.Content;
            }

            var reason = lastAttempt.ErrorReason ?? "Gemini не указал причину.";
            var fileName = Path.GetFileName(imagePath);

            _logger.LogWarning(
                "Page translation failed ({Attempt}/{Max}) for {File}. Retryable: {Retryable}. Reason: {Reason}",
                attempt,
                MaxPageAttempts,
                fileName,
                lastAttempt.Retryable,
                reason);

            if (!lastAttempt.Retryable)
            {
                break;
            }

            var delay = CalculatePageRetryDelay(attempt);
            _logger.LogInformation("Waiting {Delay} before retrying page {File}.", delay, fileName);
            await Task.Delay(delay, cancellationToken);
        }

        var failureReason = lastAttempt?.ErrorReason ?? "Gemini не вернул результат.";
        throw new InvalidOperationException($"Gemini не смог перевести страницу '{Path.GetFileName(imagePath)}': {failureReason}");
    }

    private static TimeSpan CalculatePageRetryDelay(int attempt)
    {
        var seconds = Math.Min(180, 12 * Math.Pow(1.5, attempt));
        var jitter = Random.Shared.NextDouble() * 2;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static async Task<string> CopyImageForStaticHostingAsync(string imagePath, string htmlOutputFolder, CancellationToken cancellationToken)
    {
        var staticFolder = Path.Combine(htmlOutputFolder, "imgs");
        Directory.CreateDirectory(staticFolder);

        var fileName = Path.GetFileName(imagePath);
        var destinationPath = Path.Combine(staticFolder, fileName);

        await using var sourceStream = File.OpenRead(imagePath);
        await using var destinationStream = File.Create(destinationPath);
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);

        return Path.Combine("imgs", fileName).Replace('\\', '/');
    }

    private async Task<GeminiTranslationResult> GetGeminiTranslationAsync(string imagePath, string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            const string reason = "API ключ Gemini не задан (параметр Gemini:ApiKey).";
            _logger.LogWarning("Gemini API key is not configured. Set 'Gemini:ApiKey' in appsettings.json.");
            return new GeminiTranslationResult(FormatGeminiError(reason), false, false, reason);
        }

        var url = $"https://generativelanguage.googleapis.com/v1/models/{MODEL_NAME}:generateContent?key={_apiKey}";

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var base64 = Convert.ToBase64String(imageBytes);
        var mimeType = ResolveMimeType(imagePath);

        const int MaxAttempts = 6;
        GeminiTranslationResult? lastFailure = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 0)
            {
                var delay = CalculateRetryDelay(attempt);
                _logger.LogInformation("Gemini retry {Attempt}/{Max}. Waiting {Delay} before next call.", attempt + 1, MaxAttempts, delay);
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
                var client = _httpClientFactory.CreateClient(nameof(GeminiTranslationService));
                client.Timeout = TimeSpan.FromSeconds(300);

                var request = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { text = prompt },
                                new { inlineData = new { mimeType, data = base64 } }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        maxOutputTokens = 8192, // Максимально возможное значение для gemini-2.5-flash
                        temperature = 0.7 // Также помогает против RECITATION, как обсуждали ранее
                    }
                };

                var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, httpContent, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var transient = status == 408 || status == 409 || status == 429 || status >= 500;
                    var reason = $"Gemini API error {status}: {responseContent}";
                    lastFailure = new GeminiTranslationResult(FormatGeminiError(reason), false, transient, reason);

                    if (transient && attempt < MaxAttempts - 1)
                    {
                        _logger.LogWarning("Transient Gemini error {Status}. Attempt {Attempt}/{Max}. Body: {Body}", status, attempt + 1, MaxAttempts, responseContent);
                        continue;
                    }

                    return lastFailure;
                }

                using var doc = JsonDocument.Parse(responseContent);

                if (TryExtractGeminiText(doc, out var text))
                {
                    if (IsValidTranslationMarkdown(text, out var validationIssue))
                    {
                        return new GeminiTranslationResult(text, true, false, null);
                    }

                    lastFailure = new GeminiTranslationResult(FormatGeminiError(validationIssue), false, true, validationIssue);

                    if (attempt < MaxAttempts - 1)
                    {
                        _logger.LogWarning("Gemini returned malformed translation. Attempt {Attempt}/{Max}. Reason: {Reason}", attempt + 1, MaxAttempts, validationIssue);
                        continue;
                    }

                    return lastFailure;
                }

                var (feedbackMessage, retryable) = ExtractGeminiFeedback(doc);
                lastFailure = new GeminiTranslationResult(FormatGeminiError(feedbackMessage), false, retryable, feedbackMessage);

                if (retryable && attempt < MaxAttempts - 1)
                {
                    _logger.LogWarning("Gemini returned empty content. Attempt {Attempt}/{Max}. Reason: {Reason}", attempt + 1, MaxAttempts, feedbackMessage);
                    continue;
                }

                return lastFailure;
            }
            catch (JsonException jsonEx)
            {
                var reason = $"Некорректный JSON от Gemini: {jsonEx.Message}";
                lastFailure = new GeminiTranslationResult(FormatGeminiError(reason), false, true, reason);
                _logger.LogWarning(jsonEx, "Gemini JSON parse error (attempt {Attempt}/{Max}).", attempt + 1, MaxAttempts);
            }
            catch (HttpRequestException httpEx)
            {
                var reason = httpEx.Message;
                lastFailure = new GeminiTranslationResult(FormatGeminiError(reason), false, true, reason);
                _logger.LogWarning(httpEx, "Gemini HTTP error (attempt {Attempt}/{Max}).", attempt + 1, MaxAttempts);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                const string reason = "Запрос к Gemini превысил лимит ожидания.";
                lastFailure = new GeminiTranslationResult(FormatGeminiError(reason), false, true, reason);
                _logger.LogWarning(ex, "Gemini timeout (attempt {Attempt}/{Max}).", attempt + 1, MaxAttempts);
            }
        }

        const string fallbackReason = "Превышено число повторных попыток.";
        return lastFailure ?? new GeminiTranslationResult(FormatGeminiError(fallbackReason), false, true, fallbackReason);
    }

    private static string ResolveMimeType(string imagePath)
    {
        var extension = Path.GetExtension(imagePath)?.ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static TimeSpan CalculateRetryDelay(int attempt)
    {
        var seconds = Math.Min(60, 4 * Math.Pow(1.6, attempt));
        var jitterMilliseconds = Random.Shared.Next(250, 750);
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
    }

    private static bool TryExtractGeminiText(JsonDocument doc, out string text)
    {
        text = string.Empty;

        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (var candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("content", out var content) ||
                !content.TryGetProperty("parts", out var parts) ||
                parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var partText))
                {
                    var chunk = partText.GetString();
                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        if (builder.Length > 0)
                        {
                            builder.AppendLine();
                        }
                        builder.Append(chunk.Trim());
                    }
                }
            }

            if (builder.Length > 0)
            {
                text = builder.ToString();
                return true;
            }
        }

        return false;
    }

    private static (string Message, bool Retryable) ExtractGeminiFeedback(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("promptFeedback", out var promptFeedback) &&
            promptFeedback.ValueKind == JsonValueKind.Object)
        {
            if (promptFeedback.TryGetProperty("blockReason", out var blockReason))
            {
                var reason = blockReason.GetString() ?? "запрос отклонён";
                return ($"Запрос заблокирован Gemini: {reason}.", false);
            }
        }

        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var finishReasonElement))
            {
                var finishReason = finishReasonElement.GetString();
                if (!string.IsNullOrWhiteSpace(finishReason))
                {
                    var retryable = !finishReason.Equals("SAFETY", StringComparison.OrdinalIgnoreCase) &&
                                      !finishReason.Equals("CONTENT_FILTER", StringComparison.OrdinalIgnoreCase);

                    // RECITATION теперь считается переповторяемой,
                    // так как повторный запрос может сгенерировать другую последовательность,
                    // которая не вызовет флаг RECITATION.

                    return ($"Модель завершила ответ с причиной: {finishReason}.", retryable);
                }
            }
        }

        return ("Gemini не вернул текст.", true);
    }

    private sealed record GeminiTranslationResult(string Content, bool Success, bool Retryable, string? ErrorReason);

    private static string FormatGeminiError(string reason)
        => $"| ERROR: No valid translation returned. | ОШИБКА: {reason} |";

    private static bool IsValidTranslationMarkdown(string text, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "Gemini вернул пустой ответ.";
            return false;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tableRows = new List<string[]>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains('|', StringComparison.Ordinal) || trimmed.Contains("---", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = trimmed.Split('|', StringSplitOptions.TrimEntries)
                .Where(cell => !string.IsNullOrWhiteSpace(cell))
                .ToArray();

            if (cells.Length >= 2)
            {
                tableRows.Add(cells);
            }
        }

        if (tableRows.Count == 0)
        {
            reason = "Gemini не вернул таблицу с переводом.";
            return false;
        }

        var dataRows = tableRows
            .Where(row => !row.Any(ContainsHeaderKeyword))
            .ToList();

        if (dataRows.Count < 2)
        {
            reason = "В ответе Gemini недостаточно строк перевода.";
            return false;
        }

        var originalChars = dataRows.Sum(row => row[0].Length);
        if (originalChars < 80)
        {
            reason = "Слишком мало исходного текста в ответе Gemini.";
            return false;
        }

        // Новая проверка на маркер завершения
        if (!text.Contains("[END_OF_TRANSLATION]", StringComparison.Ordinal))
        {
            reason = "Gemini вернул обрезанный ответ: отсутствует маркер завершения [END_OF_TRANSLATION].";
            // Важно: в этом случае вы должны вернуть false, чтобы сработала retry-логика
            return false;
        }

        // Удаляем маркер завершения из текста, чтобы он не попал в HTML
        text = text.Replace("[END_OF_TRANSLATION]", "", StringComparison.Ordinal);

        return true;
    }

    private static bool ContainsHeaderKeyword(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        return lowered.Contains("оригинал", StringComparison.Ordinal) ||
               lowered.Contains("original", StringComparison.Ordinal) ||
               lowered.Contains("column", StringComparison.Ordinal) ||
               lowered.Contains("перевод", StringComparison.Ordinal) ||
               lowered.Contains("translation", StringComparison.Ordinal);
    }

    private static async Task CreateIndexHtmlAsync(string htmlOutputFolder, CancellationToken cancellationToken)
    {
        var indexHtml = @"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Bookiway · Перевод</title>
    <style>
        body {
            margin: 0;
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", ""Inter"", sans-serif;
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            background: #0f172a;
            color: #e2e8f0;
        }
        body.light-mode {
            background: #f8fafc;
            color: #0f172a;
        }
        .message {
            text-align: center;
            padding: 2rem 2.5rem;
            border-radius: 24px;
            background: rgba(15, 23, 42, 0.45);
            font-size: 1.1rem;
        }
        body.light-mode .message {
            background: rgba(255, 255, 255, 0.9);
        }
    </style>
</head>
<body>
    <div class=""message"">
        <p>Пожалуйста, подождите несколько секунд...</p>
    </div>
    <script>
        const storedTheme = localStorage.getItem('theme') || 'dark';
        if (storedTheme === 'light') {
            document.body.classList.add('light-mode');
        }
        const lastPage = parseInt(localStorage.getItem('lastReadPage') || '1', 10);
        const safePage = Number.isFinite(lastPage) && lastPage > 0 ? lastPage : 1;
        const formatted = safePage.toString().padStart(4, '0');
        window.location.replace(`page-${formatted}.html`);
    </script>
</body>
</html>";
        var outputPath = Path.Combine(htmlOutputFolder, "index.html");
        await File.WriteAllTextAsync(outputPath, indexHtml, Encoding.UTF8, cancellationToken);
    }

    private static async Task CreateHtmlPageAsync(string markdownContent, int pageIndex, int totalPages, string htmlOutputFolder, string originalFileName, string originalImageRelativePath, CancellationToken cancellationToken)
    {
        var hasError = markdownContent.Contains("| ERROR: No valid translation returned.", StringComparison.Ordinal);
        var safeOriginalName = WebUtility.HtmlEncode(originalFileName);

        string? errorMessage = null;
        if (hasError)
        {
            var errorLine = markdownContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.Contains("| ОШИБКА:", StringComparison.Ordinal));
            if (errorLine is not null)
            {
                var parsed = errorLine.Split('|', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(part => part.StartsWith("ОШИБКА:", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    errorMessage = parsed.Replace("ОШИБКА:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                }
            }

            errorMessage ??= "Перевод не получен. Проверьте консоль сервиса.";
        }

        var rowsBuilder = new StringBuilder();
        if (hasError)
        {
            var safeError = WebUtility.HtmlEncode(errorMessage);
            rowsBuilder.AppendLine($$"""
<tr>
    <td colspan="2" class="error-message">
        ❌ СТРАНИЦА НЕ ПЕРЕВЕДЕНА ❌<br><br>
        Причина: {{safeError}}<br><br>
        Обратите внимание на файл: <strong>{{safeOriginalName}}</strong>
    </td>
</tr>
""");
        }
        else
        {
            var lines = markdownContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                    !line.Contains("---", StringComparison.Ordinal) &&
                    !line.Contains("1-я колонка", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("Оригинальный текст", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var line in lines)
            {
                var cells = line.Split('|', StringSplitOptions.TrimEntries)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToArray();

                if (cells.Length >= 2)
                {
                    var original = WebUtility.HtmlEncode(cells[0]);
                    var translation = WebUtility.HtmlEncode(cells[1]);
                    rowsBuilder.AppendLine("<tr>");
                    rowsBuilder.AppendLine($"<td>{original}</td>");
                    rowsBuilder.AppendLine($"<td>{translation}</td>");
                    rowsBuilder.AppendLine("</tr>");
                }
            }

            if (rowsBuilder.Length == 0)
            {
                rowsBuilder.AppendLine("<tr><td colspan=\"2\">Перевод отсутствует.</td></tr>");
            }
        }

        var rowsMarkup = rowsBuilder.ToString();
        var headerRow = hasError
            ? string.Empty
            : """
                <tr>
                    <th>Оригинальный текст</th>
                    <th>Перевод</th>
                </tr>
                """;

        var prevIndex = pageIndex - 1;
        var nextIndex = pageIndex + 1;
        var prevLink = prevIndex >= 1 ? $"page-{prevIndex:D4}.html" : "#";
        var nextLink = nextIndex <= totalPages ? $"page-{nextIndex:D4}.html" : "#";
        var prevDisabled = prevIndex < 1 ? " disabled" : string.Empty;
        var nextDisabled = nextIndex > totalPages ? " disabled" : string.Empty;

        var safeImagePath = string.IsNullOrWhiteSpace(originalImageRelativePath)
            ? string.Empty
            : originalImageRelativePath.Replace("\\", "/");
        var safeImagePathAttribute = WebUtility.HtmlEncode(safeImagePath);

        var originalButtonMarkup = string.IsNullOrEmpty(safeImagePathAttribute)
            ? "<button type=\"button\" class=\"view-original\" disabled>Оригинал недоступен</button>"
            : $"<button type=\"button\" class=\"view-original\" data-image=\"{safeImagePathAttribute}\" onclick=\"openOriginal(this)\">Открыть оригинал</button>";

        var currentPageFileName = $"page-{pageIndex:D4}.html";
        var outputPath = Path.Combine(htmlOutputFolder, currentPageFileName);

        var htmlContent = $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Перевод книги - Страница {{pageIndex}} из {{totalPages}}</title>
    <style>
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            margin: 0;
            background-color: #f7f7f7;
            color: #333;
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }
        body.dark-mode {
            background-color: #1a1a1a;
            color: #ccc;
        }
        .container {
            background-color: #fff;
            flex: 1;
            display: flex;
            flex-direction: column;
        }
        body.dark-mode .container {
            background-color: #2c2c2c;
        }
        .top-navigation,
        .bottom-navigation {
            display: flex;
            justify-content: space-between;
            align-items: center;
            padding: 8px 5px;
            background-color: #eee;
            border-bottom: 1px solid #ddd;
            flex-wrap: wrap;
            gap: 8px;
        }
        .bottom-navigation {
            border-top: 1px solid #ddd;
            border-bottom: none;
        }
        body.dark-mode .top-navigation,
        body.dark-mode .bottom-navigation {
            background-color: #3a3a3a;
            border-color: #555;
        }
        .page-info {
            flex: 1 1 240px;
        }
        .page-header {
            font-size: 1.4em;
            font-weight: 600;
            margin: 0;
        }
        .source-info {
            font-size: 0.8em;
            color: #888;
            margin: 2px 0 0;
        }
        body.dark-mode .source-info {
            color: #aaa;
        }
        .navigation-buttons {
            display: flex;
            gap: 8px;
            flex-shrink: 0;
        }
        .navigation-buttons a {
            text-decoration: none;
            padding: 6px 12px;
            border: 1px solid #ccc;
            border-radius: 4px;
            color: inherit;
        }
        .navigation-buttons a[disabled] {
            opacity: 0.5;
            pointer-events: none;
        }
        body.dark-mode .navigation-buttons a {
            border-color: #666;
        }
        .go-to-page-controls {
            display: flex;
            align-items: center;
            gap: 5px;
        }
        .go-to-page-controls input {
            width: 60px;
            padding: 5px;
            border-radius: 5px;
            border: 1px solid #ccc;
            text-align: center;
        }
        .go-to-page-controls button {
            padding: 5px 10px;
            border-radius: 5px;
            border: 1px solid #ccc;
            cursor: pointer;
        }
        body.dark-mode .go-to-page-controls input,
        body.dark-mode .go-to-page-controls button {
            border-color: #555;
            background-color: #444;
            color: #ccc;
        }
        .extra-controls {
            display: flex;
            align-items: center;
            gap: 6px;
        }
        #themeToggle,
        #themeToggleBottom {
            background: none;
            border: none;
            font-size: 1.4em;
            cursor: pointer;
            padding: 0 10px;
            color: #444;
        }
        body.dark-mode #themeToggle,
        body.dark-mode #themeToggleBottom {
            color: #f0c451;
        }
        .view-original {
            padding: 6px 14px;
            border-radius: 5px;
            border: 1px solid #0d6efd;
            background-color: #0d6efd;
            color: #fff;
            cursor: pointer;
        }
        .view-original:disabled {
            background-color: #999;
            border-color: #888;
            cursor: not-allowed;
        }
        body.dark-mode .view-original {
            border-color: #4a90e2;
            background-color: #4a90e2;
        }
        .translation-table {
            width: 100%;
            border-collapse: collapse;
            flex: 1;
        }
        .translation-table td,
        .translation-table th {
            padding: 8px 5px;
            border: 1px solid #e0e0e0;
            text-align: left;
            vertical-align: top;
        }
        .translation-table th {
            background-color: #f0f8ff;
            font-weight: bold;
            color: #1a1a1a;
        }
        body.dark-mode .translation-table td,
        body.dark-mode .translation-table th {
            border-color: #555;
        }
        body.dark-mode .translation-table th {
            background-color: #4a4a4a;
            color: #ccc;
        }
        .error-message {
            background-color: #ffe0e0;
            color: #cc0000;
            font-weight: bold;
            text-align: center;
            padding: 20px;
            font-size: 1.05em;
            line-height: 1.6;
        }
        body.dark-mode .error-message {
            background-color: #550000;
            color: #ffcccc;
        }
        @media (max-width: 600px) {
            .top-navigation,
            .bottom-navigation {
                flex-direction: column;
                align-items: stretch;
            }
            .page-info {
                flex: none;
                width: 100%;
            }
            .navigation-buttons,
            .go-to-page-controls,
            .extra-controls {
                width: 100%;
                justify-content: space-between;
            }
            .go-to-page-controls input {
                flex: 1;
            }
        }
        table {
            width: 100%;
        }
    </style>
</head>
<body onload="loadTheme(); saveCurrentPage();">
    <div class="container">
        <div class="top-navigation">
            <div class="page-info">
                <div class="page-header">Страница {{pageIndex}} из {{totalPages}}</div>
                <p class="source-info">Источник: {{safeOriginalName}}</p>
            </div>
            <div class="navigation-buttons">
                <a href="{{prevLink}}"{{prevDisabled}}>&larr; Назад</a>
                <a href="{{nextLink}}"{{nextDisabled}}>Вперёд &rarr;</a>
            </div>
            <div class="go-to-page-controls">
                <input type="number" id="pageInputTop" min="1" max="{{totalPages}}" value="{{pageIndex}}">
                <button onclick="goToPage(document.getElementById('pageInputTop').value)">Перейти</button>
            </div>
            <div class="extra-controls">
                {{originalButtonMarkup}}
                <button id="themeToggle" onclick="toggleTheme()">🌙</button>
            </div>
        </div>
        <table class="translation-table">
            <tbody>
                {{headerRow}}
                {{rowsMarkup}}
            </tbody>
        </table>
        <div class="bottom-navigation">
            <div class="navigation-buttons">
                <a href="{{prevLink}}"{{prevDisabled}}>&larr; Назад</a>
                <a href="{{nextLink}}"{{nextDisabled}}>Вперёд &rarr;</a>
            </div>
            <div class="go-to-page-controls">
                <input type="number" id="pageInputBottom" min="1" max="{{totalPages}}" value="{{pageIndex}}">
                <button onclick="goToPage(document.getElementById('pageInputBottom').value)">Перейти</button>
            </div>
            <div class="extra-controls">
                {{originalButtonMarkup}}
                <button id="themeToggleBottom" onclick="toggleTheme()">🌙</button>
            </div>
        </div>
    </div>
    <script>
        function loadTheme() {
            const currentTheme = localStorage.getItem('theme') || 'dark';
            document.body.className = currentTheme + '-mode';
            updateThemeButtons(currentTheme);
        }
        function toggleTheme() {
            const currentTheme = localStorage.getItem('theme') || 'dark';
            const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
            localStorage.setItem('theme', newTheme);
            document.body.className = newTheme + '-mode';
            updateThemeButtons(newTheme);
        }
        function updateThemeButtons(theme) {
            const label = theme === 'dark' ? '☀️' : '🌙';
            const top = document.getElementById('themeToggle');
            const bottom = document.getElementById('themeToggleBottom');
            if (top) top.textContent = label;
            if (bottom) bottom.textContent = label;
        }
        function saveCurrentPage() {
            localStorage.setItem('lastReadPage', {{pageIndex}});
        }
        function goToPage(pageNumber) {
            const totalPages = {{totalPages}};
            let pageNum = parseInt(pageNumber, 10);
            if (isNaN(pageNum) || pageNum < 1) {
                pageNum = 1;
            } else if (pageNum > totalPages) {
                pageNum = totalPages;
            }
            localStorage.setItem('lastReadPage', pageNum);
            const formattedPage = pageNum.toString().padStart(4, '0');
            window.location.href = `page-${formattedPage}.html`;
        }
        function openOriginal(button) {
            const path = button?.getAttribute('data-image');
            if (path) {
                window.open(path, '_blank');
            }
        }
    </script>
</body>
</html>
""";

        await File.WriteAllTextAsync(outputPath, htmlContent, Encoding.UTF8, cancellationToken);
    }
}
