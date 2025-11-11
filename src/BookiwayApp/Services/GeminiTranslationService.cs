using System.Net.Http;
using System;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace BookiwayApp.Services;

public sealed class GeminiTranslationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiTranslationService> _logger;
    private readonly string _apiKey;

    private const string MODEL_NAME = "gemini-2.5-flash";
    public const string DefaultPrompt = """
Ты — переводчик художественных и деловых текстов. Выполняй строковый перевод таблицы из двух колонок:
1. В первой колонке сохраняй оригинальные фразы как есть.
2. Во второй колонке размещай переведённый текст.
В требования входит:
- Сохраняй регистр, выделение и переносы строки, если это влияет на смысл.
- Не добавляй собственных комментариев.
- Если текст нельзя прочитать, пометь ячейку как «[неразборчиво]».
- Используй современной литературный русский язык без сленга.
""";

    public GeminiTranslationService(IHttpClientFactory httpClientFactory, ILogger<GeminiTranslationService> logger, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _apiKey = configuration["Gemini:ApiKey"] ?? string.Empty;
    }

    public async Task<int> TranslateRangeAsync(string imagesDirectory, int startPage, int endPage, string htmlOutputDirectory, string? promptText = null, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
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

        var files = new List<(string Path, int Page)>();
        foreach (var path in Directory.GetFiles(imagesDirectory, "page-*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path); // page-001
            var parts = name.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[1], out var pageNum))
            {
                if (pageNum >= startPage && pageNum <= endPage)
                {
                    files.Add((path, pageNum));
                }
            }
        }

        files.Sort((a, b) => a.Page.CompareTo(b.Page));

        var total = files.Count;
        if (total == 0)
        {
            return 0;
        }

        var effectivePrompt = string.IsNullOrWhiteSpace(promptText) ? DefaultPrompt : promptText!;

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagePath = files[i].Path;
            var pageIndexInSelection = i + 1;

            var translation = await GetGeminiTranslationAsync(imagePath, effectivePrompt, cancellationToken);
            var relativeImagePath = await CopyImageForStaticHostingAsync(imagePath, htmlOutputDirectory, cancellationToken);

            await CreateHtmlPageAsync(
                translation,
                pageIndexInSelection,
                total,
                htmlOutputDirectory,
                Path.GetFileName(imagePath),
                relativeImagePath,
                cancellationToken);

            progress?.Report((double)pageIndexInSelection / total);

            if (pageIndexInSelection < total)
            {
                await Task.Delay(2000, cancellationToken);
            }
        }

        await CreateIndexHtmlAsync(htmlOutputDirectory, cancellationToken);
        return total;
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

    private async Task<string> GetGeminiTranslationAsync(string imagePath, string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogWarning("Gemini API key is not configured. Set 'Gemini:ApiKey' in appsettings.json.");
            return "| ERROR: No valid translation returned. | ОШИБКА: API ключ не задан (Gemini:ApiKey). |";
        }

        var url = $"https://generativelanguage.googleapis.com/v1/models/{MODEL_NAME}:generateContent?key={_apiKey}";

        var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        var base64 = Convert.ToBase64String(imageBytes);

        const int MaxRetries = 3;
        const int RetryDelaySeconds = 5;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds), cancellationToken);
                }

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
                                new { inlineData = new { mimeType = "image/png", data = base64 } }
                            }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, httpContent, cancellationToken);
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    if (code == 429 || code >= 500)
                    {
                        _logger.LogWarning("Temporary Gemini API error {Status}. Will retry.", code);
                        continue;
                    }
                    throw new HttpRequestException($"Gemini API error {code}: {responseContent}");
                }

                using var doc = JsonDocument.Parse(responseContent);

                if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                    candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    var candidate = candidates[0];
                    if (candidate.TryGetProperty("content", out var content) &&
                        content.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0 &&
                        parts[0].TryGetProperty("text", out var textElement))
                    {
                        return textElement.GetString() ?? string.Empty;
                    }
                }

                var feedback = "Неизвестная ошибка API.";
                if (doc.RootElement.TryGetProperty("promptFeedback", out var promptFeedback))
                {
                    if (promptFeedback.TryGetProperty("blockReason", out var blockReason))
                    {
                        feedback = $"Блокировка: {blockReason.GetString()}";
                    }
                    else if (promptFeedback.TryGetProperty("finishReason", out var finishReason) && finishReason.GetString() != "STOP")
                    {
                        feedback = $"Ответ завершен с причиной: {finishReason.GetString()}";
                    }
                }

                return $"| ERROR: No valid translation returned. | ОШИБКА: Перевод не получен. {feedback} |";
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogWarning(ex, "Network error while calling Gemini. Attempt {Attempt}/{Max}.", attempt + 1, MaxRetries);
                if (attempt == MaxRetries - 1)
                {
                    return "| ERROR: No valid translation returned. | ОШИБКА: Превышено число повторных попыток. Таймаут. |";
                }
            }
        }

        return "| ERROR: No valid translation returned. | ОШИБКА: Превышено число повторных попыток. |";
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
        <p>Подготавливаем последнюю страницу…</p>
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

        string tableContent;
        if (hasError)
        {
            tableContent = $$"""
<div class="error-card">
    <p class="error-title">Не удалось получить перевод</p>
    <p>Gemini не вернул корректный результат. Попробуйте ещё раз.</p>
    <p>Файл: {{safeOriginalName}}</p>
</div>
""";
        }
        else
        {
            var rowsBuilder = new StringBuilder();
            var lines = markdownContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.TrimStart().StartsWith("---", StringComparison.Ordinal))
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
                rowsBuilder.AppendLine("<tr><td colspan=\"2\">Содержимое отсутствует.</td></tr>");
            }

            var rowsMarkup = rowsBuilder.ToString();
            tableContent = $$"""
<table class="translation-table">
    <thead>
        <tr>
            <th>Оригинал</th>
            <th>Перевод</th>
        </tr>
    </thead>
    <tbody>
        {{rowsMarkup}}
    </tbody>
</table>
""";
        }

        var prevIndex = pageIndex - 1;
        var nextIndex = pageIndex + 1;
        var prevHref = prevIndex >= 1 ? $"page-{prevIndex:D4}.html" : "#";
        var nextHref = nextIndex <= totalPages ? $"page-{nextIndex:D4}.html" : "#";
        var prevDisabled = prevIndex < 1 ? " disabled" : string.Empty;
        var nextDisabled = nextIndex > totalPages ? " disabled" : string.Empty;

        var safeImagePath = string.IsNullOrWhiteSpace(originalImageRelativePath)
            ? string.Empty
            : originalImageRelativePath.Replace("\\", "/");
        var safeImagePathAttribute = WebUtility.HtmlEncode(safeImagePath);

        var originalButton = string.IsNullOrEmpty(safeImagePathAttribute)
            ? "<button type=\"button\" class=\"pill-button\" disabled>Оригинал недоступен</button>"
            : $"<button type=\"button\" class=\"pill-button\" data-image=\"{safeImagePathAttribute}\" onclick=\"openOriginal(this)\">Посмотреть оригинал</button>";

        var currentPageFileName = $"page-{pageIndex:D4}.html";
        var outputPath = Path.Combine(htmlOutputFolder, currentPageFileName);

        var html = $$"""
<!DOCTYPE html>
<html lang="ru">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Bookiway · Страница {{pageIndex}} из {{totalPages}}</title>
    <style>
        body {
            margin: 0;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Inter", sans-serif;
            background: #0f172a;
            color: #e2e8f0;
            transition: background 0.2s ease, color 0.2s ease;
        }
        body.light-mode {
            background: #f8fafc;
            color: #0f172a;
        }
        .page {
            max-width: 960px;
            margin: 0 auto;
            padding: 2.5rem 1.5rem 3rem;
            display: flex;
            flex-direction: column;
            gap: 1.5rem;
        }
        .page-header {
            display: flex;
            justify-content: space-between;
            gap: 1rem;
            flex-wrap: wrap;
            align-items: flex-start;
        }
        .brand {
            text-transform: uppercase;
            letter-spacing: 0.2em;
            font-size: 0.8rem;
            color: #94a3b8;
            margin: 0 0 0.35rem 0;
        }
        .page-header h1 {
            margin: 0;
            font-size: clamp(1.4rem, 2.5vw, 2rem);
        }
        .meta {
            margin: 0.35rem 0 0;
            color: #94a3b8;
            font-size: 0.95rem;
        }
        .header-actions {
            display: flex;
            gap: 0.5rem;
            flex-wrap: wrap;
            align-items: center;
        }
        .pill-button {
            border: none;
            border-radius: 999px;
            padding: 0.6rem 1.4rem;
            font-weight: 600;
            cursor: pointer;
            background: rgba(226, 232, 240, 0.15);
            color: inherit;
        }
        .pill-button:disabled {
            opacity: 0.4;
            cursor: not-allowed;
        }
        .navigator {
            display: flex;
            gap: 1rem;
            flex-wrap: wrap;
            align-items: center;
            justify-content: space-between;
            border-radius: 20px;
            padding: 1rem 1.25rem;
            background: rgba(15, 23, 42, 0.35);
        }
        body.light-mode .navigator {
            background: rgba(15, 23, 42, 0.05);
        }
        .navigator.stacked {
            justify-content: center;
        }
        .nav-link {
            color: inherit;
            text-decoration: none;
            font-weight: 600;
        }
        .nav-link.disabled {
            opacity: 0.4;
            pointer-events: none;
        }
        .goto {
            display: flex;
            gap: 0.5rem;
            align-items: center;
        }
        .goto input {
            width: 90px;
            padding: 0.45rem 0.6rem;
            border-radius: 12px;
            border: 1px solid rgba(226, 232, 240, 0.4);
            background: transparent;
            color: inherit;
        }
        .goto button {
            border: none;
            border-radius: 12px;
            padding: 0.45rem 1rem;
            font-weight: 600;
            cursor: pointer;
        }
        body.light-mode .goto input {
            border-color: rgba(15, 23, 42, 0.2);
        }
        .translation-section {
            border-radius: 28px;
            padding: 1.5rem;
            background: rgba(15, 23, 42, 0.4);
        }
        body.light-mode .translation-section {
            background: #fff;
        }
        .translation-table {
            width: 100%;
            border-collapse: collapse;
        }
        .translation-table th,
        .translation-table td {
            border: 1px solid rgba(226, 232, 240, 0.2);
            padding: 0.9rem;
            vertical-align: top;
            text-align: left;
        }
        body.light-mode .translation-table th,
        body.light-mode .translation-table td {
            border-color: rgba(15, 23, 42, 0.08);
        }
        .translation-table th {
            text-transform: uppercase;
            font-size: 0.85rem;
            letter-spacing: 0.08em;
        }
        .error-card {
            border-radius: 20px;
            padding: 1.5rem;
            background: rgba(239, 68, 68, 0.1);
            border: 1px solid rgba(239, 68, 68, 0.4);
        }
        .error-title {
            margin-top: 0;
            font-weight: 700;
        }
        @media (max-width: 640px) {
            .page {
                padding: 1.5rem 1rem 2rem;
            }
            .navigator {
                flex-direction: column;
                align-items: stretch;
            }
            .goto {
                width: 100%;
            }
            .goto input {
                flex: 1;
            }
            .header-actions {
                width: 100%;
                justify-content: flex-start;
            }
        }
    </style>
</head>
<body onload="initializePage()">
    <div class="page">
        <header class="page-header">
            <div>
                <p class="brand">Bookiway</p>
                <h1>Страница {{pageIndex}} из {{totalPages}}</h1>
                <p class="meta">Оригинальный файл: {{safeOriginalName}}</p>
            </div>
            <div class="header-actions">
                <button type="button" class="pill-button theme-toggle" id="themeToggleTop" onclick="toggleTheme()">&#9728;</button>
                {{originalButton}}
            </div>
        </header>
        <nav class="navigator">
            <a href="{{prevHref}}" class="nav-link{{prevDisabled}}" aria-disabled="{{(prevIndex < 1).ToString().ToLowerInvariant()}}">&larr; Назад</a>
            <div class="goto">
                <input type="number" id="pageInputTop" min="1" max="{{totalPages}}" value="{{pageIndex}}">
                <button type="button" onclick="goToPage(document.getElementById('pageInputTop').value)">Перейти</button>
            </div>
            <a href="{{nextHref}}" class="nav-link{{nextDisabled}}" aria-disabled="{{(nextIndex > totalPages).ToString().ToLowerInvariant()}}">Вперёд &rarr;</a>
        </nav>
        <section class="translation-section">
            {{tableContent}}
        </section>
        <nav class="navigator stacked">
            <a href="{{prevHref}}" class="nav-link{{prevDisabled}}" aria-disabled="{{(prevIndex < 1).ToString().ToLowerInvariant()}}">&larr; Назад</a>
            <button type="button" class="pill-button theme-toggle" id="themeToggleBottom" onclick="toggleTheme()">&#9728;</button>
            <div class="goto">
                <input type="number" id="pageInputBottom" min="1" max="{{totalPages}}" value="{{pageIndex}}">
                <button type="button" onclick="goToPage(document.getElementById('pageInputBottom').value)">Перейти</button>
            </div>
            <a href="{{nextHref}}" class="nav-link{{nextDisabled}}" aria-disabled="{{(nextIndex > totalPages).ToString().ToLowerInvariant()}}">Вперёд &rarr;</a>
        </nav>
    </div>
    <script>
        function initializePage() {
            loadTheme();
            saveCurrentPage();
        }
        function loadTheme() {
            const theme = localStorage.getItem('theme') || 'dark';
            document.body.className = theme + '-mode';
            updateThemeLabels(theme);
        }
        function toggleTheme() {
            const current = localStorage.getItem('theme') || 'dark';
            const next = current === 'dark' ? 'light' : 'dark';
            localStorage.setItem('theme', next);
            document.body.className = next + '-mode';
            updateThemeLabels(next);
        }
        function updateThemeLabels(theme) {
            const label = theme === 'dark' ? '\u2600' : '\u263E';
            document.querySelectorAll('.theme-toggle').forEach(btn => btn.textContent = label);
        }
        function saveCurrentPage() {
            localStorage.setItem('lastReadPage', {{pageIndex}});
        }
        function goToPage(value) {
            const total = {{totalPages}};
            let target = parseInt(value, 10);
            if (isNaN(target) || target < 1) { target = 1; }
            if (target > total) { target = total; }
            localStorage.setItem('lastReadPage', target);
            const formatted = target.toString().padStart(4, '0');
            window.location.href = `page-${formatted}.html`;
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
        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8, cancellationToken);
    }
}
