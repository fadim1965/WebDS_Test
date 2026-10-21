namespace WebDS_Test.Services.Images;

/// <summary>
/// The default store: images become files in a folder the app serves as static content. Nothing the
/// client supplies reaches the filesystem — the name is generated, the extension is checked against
/// the sniffed bytes, and the size is bounded while reading — so a hostile upload cannot pick a path
/// or masquerade as an image.
/// </summary>
public sealed class LocalImageStore : IImageStore
{
    private readonly ImageStorageOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<LocalImageStore> _logger;

    public LocalImageStore(
        IOptions<ImageStorageOptions> options,
        IHostEnvironment environment,
        ILogger<LocalImageStore> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        RequestPath = "/" + _options.RequestPath.Trim('/', '\\', ' ');
    }

    public string RequestPath { get; }

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();

        if (!_options.AllowedExtensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ImageRejectedException(
                $"That file type is not accepted. Allowed image types: {string.Join(", ", _options.AllowedExtensions)}.");
        }

        var bytes = await ReadBoundedAsync(content, ct);

        if (bytes.Length == 0)
        {
            throw new ImageRejectedException("That file was empty.");
        }

        if (bytes.Length > _options.MaxBytes)
        {
            throw new ImageRejectedException($"Images must be {Describe(_options.MaxBytes)} or smaller.");
        }

        var format = ImageFormats.Detect(bytes);

        if (format is null)
        {
            throw new ImageRejectedException("That file is not a readable image.");
        }

        if (!ImageFormats.MatchesExtension(format, extension))
        {
            throw new ImageRejectedException($"A {format.ToUpperInvariant()} image cannot use the '{extension}' extension.");
        }

        var folder = ResolveFolder();
        Directory.CreateDirectory(folder);

        // The stored name is ours: a new GUID plus the validated extension. The uploader's own file
        // name is never used, so "..\\..\\appsettings.json" is not a path, it is just an extension.
        var name = Guid.NewGuid().ToString("N") + extension;
        await File.WriteAllBytesAsync(Path.Combine(folder, name), bytes, ct);

        _logger.LogInformation("Stored uploaded image {Name} ({Bytes} bytes)", name, bytes.Length);

        return $"{RequestPath}/{name}";
    }

    public Task DeleteAsync(string url, CancellationToken ct = default)
    {
        var name = NameFor(url);

        if (name is null)
        {
            return Task.CompletedTask;
        }

        var path = Path.Combine(ResolveFolder(), name);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Removed uploaded image {Name}", name);
            }
        }
        catch (Exception ex)
        {
            // A leftover file is harmless; failing the save over it is not.
            _logger.LogWarning(ex, "Could not remove uploaded image {Name}", name);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The bare file name a URL under this store refers to, or <c>null</c> when the URL is not one of
    /// ours. Rejecting anything with separators or a parent reference keeps deletion inside the folder.
    /// </summary>
    private string? NameFor(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var prefix = RequestPath + "/";

        if (!url.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = url[prefix.Length..];

        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
        {
            return null;
        }

        return name;
    }

    private string ResolveFolder() =>
        Path.IsPathRooted(_options.Folder)
            ? _options.Folder
            : Path.GetFullPath(_options.Folder, _environment.ContentRootPath);

    /// <summary>
    /// Reads the stream, stopping once it is provably over the limit. Bounding the read is the point:
    /// a 2 GB "image" must not be buffered into memory just to be rejected.
    /// </summary>
    private async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;

        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > _options.MaxBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private static string Describe(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes / 1024} KB";
}
