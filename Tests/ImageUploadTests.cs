using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebDS_Test.Services.Images;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Covers the image-upload rules. Everything here is about trusting nothing the client sent: the
/// extension is checked, the bytes are sniffed, the size is bounded and the stored name is chosen by
/// the store — so a renamed payload, a foreign path or an oversized file cannot become a picture.
/// </summary>
public sealed class ImageUploadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "webds-img-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    // ---- fixtures ---------------------------------------------------------

    private static byte[] Png(int padding = 8) =>
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(new byte[padding]).ToArray();

    private static byte[] Jpeg() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

    private static byte[] Gif() => Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[4]).ToArray();

    private static byte[] Webp() =>
        Encoding.ASCII.GetBytes("RIFF").Concat(new byte[4]).Concat(Encoding.ASCII.GetBytes("WEBP")).ToArray();

    private static Stream Open(byte[] bytes) => new MemoryStream(bytes);

    private LocalImageStore Store(Action<ImageStorageOptions>? configure = null)
    {
        var options = new ImageStorageOptions
        {
            Folder = Path.Combine(_root, "uploads"),
            RequestPath = "/uploads"
        };

        configure?.Invoke(options);

        var environment = Mock.Of<IHostEnvironment>(e => e.ContentRootPath == _root);
        return new LocalImageStore(TestSupport.Options(options), environment, NullLogger<LocalImageStore>.Instance);
    }

    private static string StoredName(LocalImageStore store, string url) => url[(store.RequestPath.Length + 1)..];

    // ---- format sniffing --------------------------------------------------

    [Fact]
    public void Detect_RecognisesEachAllowedFormat()
    {
        Assert.Equal("png", ImageFormats.Detect(Png()));
        Assert.Equal("jpeg", ImageFormats.Detect(Jpeg()));
        Assert.Equal("gif", ImageFormats.Detect(Gif()));
        Assert.Equal("webp", ImageFormats.Detect(Webp()));
        Assert.Null(ImageFormats.Detect(Encoding.ASCII.GetBytes("<?php echo 1; ?>")));
        Assert.Null(ImageFormats.Detect(Array.Empty<byte>()));
    }

    [Fact]
    public void MatchesExtension_OnlyAcceptsTheRightNames()
    {
        Assert.True(ImageFormats.MatchesExtension("jpeg", ".jpg"));
        Assert.True(ImageFormats.MatchesExtension("jpeg", ".jpeg"));
        Assert.True(ImageFormats.MatchesExtension("png", ".PNG"));
        Assert.False(ImageFormats.MatchesExtension("png", ".jpg"));
        Assert.False(ImageFormats.MatchesExtension("gif", ".svg"));
    }

    // ---- saving -----------------------------------------------------------

    [Fact]
    public async Task Save_StoresTheFileAndReturnsAUrlUnderTheRequestPath()
    {
        var store = Store();

        var url = await store.SaveAsync(Open(Png()), "product photo.png");

        Assert.StartsWith("/uploads/", url);
        Assert.EndsWith(".png", url);

        var file = Path.Combine(_root, "uploads", StoredName(store, url));
        Assert.True(File.Exists(file));
        Assert.Equal(Png(), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Save_UsesTheClientsExtensionButNeverItsName()
    {
        var store = Store();

        // A path-traversal file name is harmless: only the extension survives.
        var url = await store.SaveAsync(Open(Png()), "..\\..\\appsettings.png");

        Assert.StartsWith("/uploads/", url);
        Assert.DoesNotContain("appsettings", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", url, StringComparison.Ordinal);
        Assert.EndsWith(".png", url);
    }

    [Fact]
    public async Task Save_GivesEachUploadADistinctName()
    {
        var store = Store();

        var first = await store.SaveAsync(Open(Png()), "same.png");
        var second = await store.SaveAsync(Open(Png()), "same.png");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "uploads")).Length);
    }

    [Theory]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("script.php", "image/png")]
    [InlineData("photo.bmp", "image/bmp")]
    public async Task Save_RejectsDisallowedExtensions(string fileName, string _)
    {
        var store = Store();

        await Assert.ThrowsAsync<ImageRejectedException>(() => store.SaveAsync(Open(Png()), fileName));
        Assert.False(Directory.Exists(Path.Combine(_root, "uploads")) && Directory.GetFiles(Path.Combine(_root, "uploads")).Length > 0);
    }

    [Fact]
    public async Task Save_RejectsANonImageWearingAnImageExtension()
    {
        var store = Store();

        // A PHP payload renamed to .png: the extension passes, the magic bytes do not.
        await Assert.ThrowsAsync<ImageRejectedException>(
            () => store.SaveAsync(Open(Encoding.UTF8.GetBytes("<?php system($_GET['c']); ?>")), "innocent.png"));
    }

    [Fact]
    public async Task Save_RejectsWhenTheBytesContradictTheExtension()
    {
        var store = Store();

        // PNG content declared as a JPEG.
        await Assert.ThrowsAsync<ImageRejectedException>(() => store.SaveAsync(Open(Png()), "photo.jpg"));
    }

    [Fact]
    public async Task Save_RejectsAnEmptyFile()
    {
        var store = Store();

        await Assert.ThrowsAsync<ImageRejectedException>(() => store.SaveAsync(Open(Array.Empty<byte>()), "empty.png"));
    }

    [Fact]
    public async Task Save_RejectsAFileOverTheLimit()
    {
        var store = Store(o => o.MaxBytes = 64);

        var tooBig = Png(padding: 200);

        await Assert.ThrowsAsync<ImageRejectedException>(() => store.SaveAsync(Open(tooBig), "big.png"));
    }

    [Fact]
    public async Task Save_AcceptsAFileAtTheLimit()
    {
        var store = Store(o => o.MaxBytes = 64);

        var url = await store.SaveAsync(Open(Png(padding: 56)), "fits.png");

        Assert.EndsWith(".png", url);
    }

    // ---- deleting ---------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesAFileTheStoreOwns()
    {
        var store = Store();
        var url = await store.SaveAsync(Open(Png()), "photo.png");
        var file = Path.Combine(_root, "uploads", StoredName(store, url));

        Assert.True(File.Exists(file));
        await store.DeleteAsync(url);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Delete_IgnoresAUrlTheStoreDoesNotOwn()
    {
        var store = Store();

        // Nothing about this is "ours"; it must be a quiet no-op, not a delete somewhere else.
        await store.DeleteAsync("/images/products/example.png");
        await store.DeleteAsync("https://cdn.example/photo.png");
        await store.DeleteAsync("");
    }

    [Fact]
    public async Task Delete_RefusesToWalkOutOfItsFolder()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "keepme.txt");
        await File.WriteAllTextAsync(outside, "safe");

        var store = Store();

        await store.DeleteAsync("/uploads/../keepme.txt");
        await store.DeleteAsync("/uploads/sub/keepme.txt");

        Assert.True(File.Exists(outside));
    }

    // ---- collecting from the form -----------------------------------------

    [Fact]
    public void ParseUrls_SplitsDedupesAndDropsBlanks()
    {
        var urls = ProductImageInput.ParseUrls("  /a.png \r\n\r\n/b.jpg\n/a.png\n   ");

        Assert.Equal(new[] { "/a.png", "/b.jpg" }, urls);
    }

    [Fact]
    public async Task Collect_KeepsTypedUrlsFirstAndAppendsUploads()
    {
        var store = new FakeImageStore();

        var urls = await ProductImageInput.CollectAsync(
            "/typed-primary.png\n/typed-second.png",
            new[] { Form("upload.png"), Form("photo.jpg") },
            store);

        Assert.Equal(
            new[] { "/typed-primary.png", "/typed-second.png", "/uploads/0-upload.png", "/uploads/1-photo.jpg" },
            urls);
        Assert.Equal(2, store.Saved.Count);
    }

    [Fact]
    public async Task Collect_SkipsEmptyFileInputs()
    {
        var store = new FakeImageStore();

        var urls = await ProductImageInput.CollectAsync(null, new[] { Form("blank.png", Array.Empty<byte>()) }, store);

        Assert.Empty(urls);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task Collect_RejectsWhenTheGalleryWouldBeTooLarge()
    {
        var store = new FakeImageStore();

        await Assert.ThrowsAsync<ImageRejectedException>(
            () => ProductImageInput.CollectAsync("a.png\nb.png", new[] { Form("c.png") }, store, maxImages: 2));

        // Whatever this call stored is rolled back so a rejected save leaves nothing behind.
        Assert.Equal(store.Saved, store.Deleted);
    }

    [Fact]
    public async Task Collect_RollsBackEarlierUploadsWhenALaterOneIsRejected()
    {
        var store = new FakeImageStore(reject: name => name == "bad.png");

        await Assert.ThrowsAsync<ImageRejectedException>(
            () => ProductImageInput.CollectAsync(null, new[] { Form("good.png"), Form("bad.png") }, store));

        Assert.Single(store.Saved);
        Assert.Equal(store.Saved, store.Deleted);
    }

    [Fact]
    public async Task RemoveOrphans_DeletesOnlyWhatIsNeitherKeptNorReused()
    {
        var store = new FakeImageStore();

        await ProductImageInput.RemoveOrphansAsync(
            new[] { "/uploads/gone.png", "/uploads/shared.png", "/uploads/edit.png" },
            new[] { "/uploads/edit.png" },
            store,
            url => Task.FromResult(url == "/uploads/shared.png"));

        Assert.Equal(new[] { "/uploads/gone.png" }, store.Deleted);
    }

    private static IFormFile Form(string name, byte[]? bytes = null) => new FakeFormFile(name, bytes ?? new byte[] { 1, 2, 3 });

    /// <summary>An image store that records calls and can be told to reject particular file names.</summary>
    private sealed class FakeImageStore : IImageStore
    {
        private readonly Func<string, bool> _reject;

        public FakeImageStore(Func<string, bool>? reject = null) => _reject = reject ?? (_ => false);

        public string RequestPath => "/uploads";

        public List<string> Saved { get; } = new();

        public List<string> Deleted { get; } = new();

        public Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct = default)
        {
            if (_reject(fileName))
            {
                throw new ImageRejectedException($"Rejected {fileName}.");
            }

            var url = $"/uploads/{Saved.Count}-{fileName}";
            Saved.Add(url);
            return Task.FromResult(url);
        }

        public Task DeleteAsync(string url, CancellationToken ct = default)
        {
            Deleted.Add(url);
            return Task.CompletedTask;
        }
    }

    /// <summary>The minimum of <see cref="IFormFile"/> the collector touches, backed by a byte array.</summary>
    private sealed class FakeFormFile : IFormFile
    {
        private readonly byte[] _bytes;

        public FakeFormFile(string fileName, byte[] bytes)
        {
            FileName = fileName;
            _bytes = bytes;
        }

        public string ContentType => "application/octet-stream";

        public string ContentDisposition => $"form-data; name=\"ImageFiles\"; filename=\"{FileName}\"";

        public IHeaderDictionary Headers { get; } = new HeaderDictionary();

        public long Length => _bytes.Length;

        public string Name => "ImageFiles";

        public string FileName { get; }

        public void CopyTo(Stream target) => target.Write(_bytes, 0, _bytes.Length);

        public Task CopyToAsync(Stream target, CancellationToken ct = default) => target.WriteAsync(_bytes, ct).AsTask();

        public Stream OpenReadStream() => new MemoryStream(_bytes);
    }
}
