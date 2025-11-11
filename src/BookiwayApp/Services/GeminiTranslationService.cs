using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace BookiwayApp.Services;

public sealed class GeminiTranslationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiTranslationService> _logger;
    private readonly string _apiKey;

    private const string MODEL_NAME = "gemini-2.5-flash";

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

        for (int i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imagePath = files[i].Path;
            var pageIndexInSelection = i + 1;

            var translation = await GetGeminiTranslationAsync(imagePath, cancellationToken);

            await CreateHtmlPageAsync(translation, pageIndexInSelection, total, htmlOutputDirectory, Path.GetFileName(imagePath), cancellationToken);

            progress?.Report((double)pageIndexInSelection / total);

            if (pageIndexInSelection < total)
            {
                await Task.Delay(2000, cancellationToken);
            }
        }

        await CreateIndexHtmlAsync(htmlOutputDirectory, cancellationToken);
        return total;
    }

    private async Task<string> GetGeminiTranslationAsync(string imagePath, CancellationToken cancellationToken)
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

                var prompt = @"Выполни параллельный перевод текста в формате таблицы.
1-я колонка: Оригинальный текст на английском языке.
2-я колонка: Перевод на русский язык.
Ключевые требования к переводу:
Естественность и Адаптация: Переводи естественно и литературно на русский язык. Адаптируй синтаксис, грамматику и лексику так, чтобы русский текст звучал понятно, грамотно и естественно для носителя языка. Категорически исключи бессмысленный дословный перевод, сохраняющий английский синтаксис.
Сленг и Идиомы: Переводи сленговые выражения, идиомы и разговорные фразы их наиболее точными, естественными и смысловыми русскими эквивалентами.
(Пример: ""I gotta go"" → ""Мне нужно идти"")
Сохранение Структуры:
Не пропускай ни одного предложения, даже короткого.
Сохраняй исходный порядок и структуру абзацев.
Не объединяй и не разделяй предложения. Каждое предложение оригинала должно соответствовать одной строке перевода.
Формат Ответа:
Твой ответ должен быть ИСКЛЮЧИТЕЛЬНО таблицей в формате Markdown.
Исключи любые вступительные, заключительные или пояснительные тексты.";

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
    <title>Начало книги</title>
</head>
<body>
    <p>Загрузка последней прочитанной страницы...</p>
    <script>
        const currentTheme = localStorage.getItem('theme') || 'dark';
        document.body.className = currentTheme + '-mode';
        const lastPage = localStorage.getItem('lastReadPage');
        let targetPage = 1;
        if (lastPage) { targetPage = parseInt(lastPage, 10); }
        const formattedPage = targetPage.toString().padStart(4, '0');
        window.location.replace(`page-${formattedPage}.html`);
    </script>
</body>
</html>";

        var outputPath = Path.Combine(htmlOutputFolder, "index.html");
        await File.WriteAllTextAsync(outputPath, indexHtml, Encoding.UTF8, cancellationToken);
    }

    private static async Task CreateHtmlPageAsync(string markdownContent, int pageIndex, int totalPages, string htmlOutputFolder, string originalFileName, CancellationToken cancellationToken)
    {
        var hasError = markdownContent.Contains("| ERROR: No valid translation returned.");
        var errorMessage = "";
        var tableRows = new StringBuilder();
        if (hasError)
        {
            var errorLine = markdownContent.Split('\n').FirstOrDefault(line => line.Contains("| ОШИБКА:"));
            if (errorLine != null)
            {
                var parts = errorLine.Split('|', StringSplitOptions.TrimEntries).Where(p => p.StartsWith("ОШИБКА:")).FirstOrDefault();
                errorMessage = (parts?.Replace("ОШИБКА: ", "") ?? "Перевод не получен. Проверьте консоль.");
            }
            tableRows.AppendLine($@"
            <tr><td colspan=""2"" class=""error-message"">
                ❌ **СТРАНИЦА НЕ ПЕРЕВЕДЕНА** ❌<br><br>
                **Причина:** {errorMessage} <br><br>
                Обратите внимание на этот файл: **{originalFileName}**
            </td></tr>");
        }
        else
        {
            var lines = markdownContent.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.Contains("---") && !line.Contains("1-я колонка") && !line.Contains("Оригинальный текст"))
                .ToList();
            foreach (var line in lines)
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                if (parts.Length >= 2)
                {
                    tableRows.AppendLine("<tr>");
                    tableRows.AppendLine($"<td>{parts[0]}</td>");
                    tableRows.AppendLine($"<td>{parts[1]}</td>");
                    tableRows.AppendLine("</tr>");
                }
            }
        }

        var prevIndex = pageIndex - 1;
        var nextIndex = pageIndex + 1;
        var prevLink = prevIndex >= 1 ? $"page-{prevIndex:D4}.html" : "#";
        var nextLink = nextIndex <= totalPages ? $"page-{nextIndex:D4}.html" : "#";
        var currentPageFileName = $"page-{pageIndex:D4}.html";
        var outputPath = Path.Combine(htmlOutputFolder, currentPageFileName);

        var html = $@"<!DOCTYPE html>
<html lang=""ru"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Перевод книги - Страница {pageIndex} из {totalPages}</title>
    <style>
        /* Полный стиль как в консольной утилите */
        body {{ 
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; 
            margin: 0; 
            background-color: #f7f7f7; 
            color: #333; 
            min-height: 100vh;
            display: flex;
            flex-direction: column;
        }}
        .container {{ background-color: #fff; }}
        .top-navigation, .bottom-navigation {{ 
            display: flex; justify-content: space-between; align-items: center; 
            padding: 8px 5px; background-color: #eee; border-bottom: 1px solid #ddd; flex-wrap: wrap; 
        }}
        .bottom-navigation {{ border-top: 1px solid #ddd; border-bottom: none; }}
        .translation-table {{ width: 100%; border-collapse: collapse; margin: 5px 0; flex-grow: 1; }}
        .translation-table td {{ padding: 8px 5px; border: 1px solid #e0e0e0; text-align: left; vertical-align: top; line-height: 1.4; width: 50%; font-size: 0.9em; }}
        .translation-table th {{ padding: 8px 5px; border: 1px solid #e0e0e0; text-align: center; background-color: #f0f8ff; font-weight: bold; color: #1a1a1a; }}
        .error-message {{ background-color: #ffe0e0; color: #cc0000; font-weight: bold; text-align: center; padding: 20px; font-size: 1.1em; line-height: 1.6; }}
        body.dark-mode {{ background-color: #1a1a1a; color: #ccc; }}
        body.dark-mode .container {{ background-color: #2c2c2c; }}
        body.dark-mode .top-navigation, body.dark-mode .bottom-navigation {{ background-color: #3a3a3a; border-bottom-color: #555; border-top-color: #555; }}
        body.dark-mode .translation-table td {{ border: 1px solid #555; }}
        body.dark-mode .translation-table th {{ background-color: #4a4a4a; color: #ccc; border: 1px solid #555; }}
        body.dark-mode .error-message {{ background-color: #550000; color: #ffcccc; }}
        #themeToggle, #themeToggleBottom {{ background: none; border: none; font-size: 1.5em; cursor: pointer; padding: 0 10px; color: #444; transition: color 0.3s; }}
        .dark-mode #themeToggle, .dark-mode #themeToggleBottom {{ color: #f0c451; }}
        .page-header {{ font-size: 1.8em; font-weight: 600; text-align: center; width: 100%; margin: 5px 0; }}
        .source-info {{ font-size: 0.7em; color: #888; text-align: center; margin-top: 0; margin-bottom: 5px; }}
        .navigation-buttons {{ display: flex; gap: 8px; flex-shrink: 0; }}
        .go-to-page-controls {{ display: flex; align-items: center; gap: 5px; }}
        .go-to-page-controls input {{ width: 45px; padding: 6px; border-radius: 5px; text-align: center; font-size: 0.85em; }}
        @media (max-width: 600px) {{
            .top-navigation, .bottom-navigation {{ flex-direction: column; gap: 5px; padding: 5px; }}
            .navigation-buttons {{ width: 100%; justify-content: space-between; }}
            .go-to-page-controls {{ width: 100%; justify-content: space-between; }}
            .go-to-page-controls input {{ width: 50%; }}
        }}
    </style>
</head>
<body onload=""loadTheme(); saveCurrentPage();"">
    <div class=""container"">
        <div class=""top-navigation"">
            <div class=""page-header"">Страница {pageIndex} из {totalPages}</div>
            <p class=""source-info"">Источник: {originalFileName}</p>
            <div class=""navigation-buttons"">
                <a href=""{prevLink}""{(prevIndex < 1 ? " disabled" : "")}>&larr; Назад</a>
                <a href=""{nextLink}""{(nextIndex > totalPages ? " disabled" : "")}>Вперед &rarr;</a>
            </div>
            <div class=""go-to-page-controls"">
                <input type=""number"" id=""pageInputTop"" min=""1"" max=""{totalPages}"" value=""{pageIndex}"" placeholder=""{pageIndex}"">
                <button onclick=""goToPage(document.getElementById('pageInputTop').value)"">Перейти</button>
            </div>
            <button id=""themeToggle"" onclick=""toggleTheme()"">🌙</button>
        </div>

        <table class=""translation-table"">
            <tbody>
                {(hasError ? "" : @"
                <tr>
                    <th>Оригинальный текст</th>
                    <th>Перевод</th>
                </tr>
                ")}
                {tableRows}
            </tbody>
        </table>

        <div class=""bottom-navigation"">
            <div class=""navigation-buttons"">
                <a href=""{prevLink}""{(prevIndex < 1 ? " disabled" : "")}>&larr; Назад</a>
                <a href=""{nextLink}""{(nextIndex > totalPages ? " disabled" : "")}>Вперед &rarr;</a>
            </div>
            <div class=""go-to-page-controls"">
                <input type=""number"" id=""pageInputBottom"" min=""1"" max=""{totalPages}"" value=""{pageIndex}"" placeholder=""{pageIndex}"">
                <button onclick=""goToPage(document.getElementById('pageInputBottom').value)"">Перейти</button>
            </div>
            <button id=""themeToggleBottom"" onclick=""toggleTheme()"">🌙</button>
        </div>
    </div>

    <script>
        function loadTheme() {{
            const currentTheme = localStorage.getItem('theme') || 'dark';
            document.body.className = currentTheme + '-mode';
            document.getElementById('themeToggle').innerText = currentTheme === 'dark' ? '☀️' : '🌙';
            document.getElementById('themeToggleBottom').innerText = currentTheme === 'dark' ? '☀️' : '🌙';
        }}
        function toggleTheme() {{
            const currentTheme = localStorage.getItem('theme') || 'dark';
            const newTheme = currentTheme === 'dark' ? 'light' : 'dark';
            localStorage.setItem('theme', newTheme);
            document.body.className = newTheme + '-mode';
            document.getElementById('themeToggle').innerText = newTheme === 'dark' ? '☀️' : '🌙';
            document.getElementById('themeToggleBottom').innerText = newTheme === 'dark' ? '☀️' : '🌙';
        }}
        function saveCurrentPage() {{ localStorage.setItem('lastReadPage', {pageIndex}); }}
        function goToPage(pageNumber) {{
            const totalPages = {totalPages};
            let pageNum = parseInt(pageNumber, 10);
            if (isNaN(pageNum) || pageNum < 1) {{ pageNum = 1; }}
            else if (pageNum > totalPages) {{ pageNum = totalPages; }}
            localStorage.setItem('lastReadPage', pageNum);
            window.location.href = `page-${{pageNum.toString().padStart(4, '0')}}.html`;
        }}
    </script>
</body>
</html>";

        await File.WriteAllTextAsync(outputPath, html, Encoding.UTF8, cancellationToken);
    }
}


