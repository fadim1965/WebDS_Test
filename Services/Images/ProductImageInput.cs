using Microsoft.AspNetCore.Http;

namespace WebDS_Test.Services.Images;

/// <summary>
/// Turns the product editor's two image inputs — a textarea of URLs and zero or more uploaded files
/// — into the single ordered URL list the gallery is rebuilt from. Uploads are appended after the
/// typed URLs, so the first line of the textarea still decides the primary image.
/// </summary>
public static class ProductImageInput
{
    /// <summary>Splits the textarea into distinct, non-blank, trimmed URLs, preserving order.</summary>
    public static IReadOnlyList<string> ParseUrls(string? imageUrls) =>
        (imageUrls ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Stores every non-empty upload and returns the combined list. If any file is rejected, the
    /// files already stored for this call are removed again so a rejected save leaves nothing behind.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CollectAsync(
        string? imageUrls,
        IEnumerable<IFormFile>? files,
        IImageStore store,
        int maxImages = 10,
        CancellationToken ct = default)
    {
        var urls = new List<string>(ParseUrls(imageUrls));
        var stored = new List<string>();

        try
        {
            foreach (var file in files ?? Enumerable.Empty<IFormFile>())
            {
                if (file is null || file.Length == 0)
                {
                    continue;
                }

                await using var stream = file.OpenReadStream();
                var url = await store.SaveAsync(stream, file.FileName, ct);

                stored.Add(url);

                if (!urls.Contains(url, StringComparer.Ordinal))
                {
                    urls.Add(url);
                }
            }
        }
        catch (ImageRejectedException)
        {
            foreach (var url in stored)
            {
                await store.DeleteAsync(url, ct);
            }

            throw;
        }

        if (urls.Count > maxImages)
        {
            foreach (var url in stored)
            {
                await store.DeleteAsync(url, ct);
            }

            throw new ImageRejectedException($"A product can have at most {maxImages} images.");
        }

        return urls;
    }

    /// <summary>
    /// Removes files for uploads that have dropped out of a product's gallery. <paramref name="stillReferenced"/>
    /// is asked about each candidate so a URL another product still points at is left alone; callers
    /// run this after the save is committed, when the product's own old rows are already gone.
    /// </summary>
    public static async Task RemoveOrphansAsync(
        IEnumerable<string> previousUrls,
        IReadOnlyCollection<string> keptUrls,
        IImageStore store,
        Func<string, Task<bool>> stillReferenced,
        CancellationToken ct = default)
    {
        foreach (var url in previousUrls.Distinct(StringComparer.Ordinal))
        {
            if (keptUrls.Contains(url, StringComparer.Ordinal) || await stillReferenced(url))
            {
                continue;
            }

            await store.DeleteAsync(url, ct);
        }
    }
}
