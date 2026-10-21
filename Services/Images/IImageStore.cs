namespace WebDS_Test.Services.Images;

/// <summary>
/// Persists uploaded product images and hands back the URL to store on a
/// <see cref="ProductImage"/>. The application only ever handles that URL, so the bytes could live
/// on disk today and in object storage later without the controllers noticing.
/// </summary>
public interface IImageStore
{
    /// <summary>The request path uploaded images are served from, e.g. <c>/uploads</c>.</summary>
    string RequestPath { get; }

    /// <summary>
    /// Validates and stores one uploaded image, returning the public URL that serves it.
    /// </summary>
    /// <exception cref="ImageRejectedException">
    /// The file is not an acceptable image — an unknown type, a format that disagrees with the file
    /// name, or a file larger than the configured limit.
    /// </exception>
    Task<string> SaveAsync(Stream content, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Removes a previously stored image, best effort. A URL the store does not own is ignored, so
    /// this can never reach outside the upload folder.
    /// </summary>
    Task DeleteAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// An upload that failed validation. The message is written for the person who chose the file, so a
/// controller can surface it as a form error unchanged.
/// </summary>
public sealed class ImageRejectedException : Exception
{
    public ImageRejectedException(string message) : base(message)
    {
    }
}
