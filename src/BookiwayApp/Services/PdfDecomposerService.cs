using System.IO;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace BookiwayApp.Services;

public sealed class PdfDecomposerService
{
    private readonly ILogger<PdfDecomposerService> _logger;

    public PdfDecomposerService(ILogger<PdfDecomposerService> logger)
    {
        _logger = logger;
    }

    public async Task<PdfDecompositionResult> DecomposeAsync(Stream pdfStream, string outputDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (pdfStream is null)
        {
            throw new ArgumentNullException(nameof(pdfStream));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("The output directory must be provided.", nameof(outputDirectory));
        }

        Directory.CreateDirectory(outputDirectory);

        await using var bufferStream = new MemoryStream();
        await pdfStream.CopyToAsync(bufferStream, cancellationToken);
        var pdfBytes = bufferStream.ToArray();

        using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(1080, 1920));
        var pageCount = docReader.GetPageCount();
        var savedFiles = new List<string>(pageCount);

        if (pageCount == 0)
        {
            return new PdfDecompositionResult(pageCount, savedFiles);
        }

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pageReader = docReader.GetPageReader(pageIndex);
            var rawBytes = pageReader.GetImage();
            var width = pageReader.GetPageWidth();
            var height = pageReader.GetPageHeight();

            using var image = Image.LoadPixelData<Bgra32>(rawBytes, width, height);
            image.Mutate(ctx => ctx.Flip(FlipMode.Vertical));

            var fileName = $"page-{pageIndex + 1:D3}.png";
            var destinationPath = Path.Combine(outputDirectory, fileName);

            await image.SaveAsPngAsync(destinationPath, cancellationToken);
            savedFiles.Add(destinationPath);

            var normalizedProgress = (pageIndex + 1d) / pageCount;
            progress?.Report(normalizedProgress);
        }

        _logger.LogInformation("PDF decomposition completed. Saved {PageCount} pages to {Directory}", pageCount, outputDirectory);

        return new PdfDecompositionResult(pageCount, savedFiles);
    }
}

public sealed record PdfDecompositionResult(int PageCount, IReadOnlyList<string> SavedFiles);
