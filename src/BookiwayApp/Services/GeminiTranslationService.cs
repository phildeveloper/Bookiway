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
                <a href="{{nextLink}}"{{nextDisabled}}>Вперед &rarr;</a>
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
                <a href="{{nextLink}}"{{nextDisabled}}>Вперед &rarr;</a>
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
