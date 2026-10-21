namespace WebDS_Test.Services.Images;

/// <summary>
/// Settings for uploaded product images. Bound from the <c>Images</c> configuration section, so the
/// folder uploads land in, the request path that serves them, and the size/type limits are all
/// deployment configuration rather than code.
/// </summary>
public sealed class ImageStorageOptions
{
    public const string SectionName = "Images";

    /// <summary>
    /// Folder uploaded images are written to. A relative value is resolved against the content root;
    /// <c>App_Data/uploads</c> (the default) keeps user files out of the source tree.
    /// </summary>
    public string Folder { get; set; } = "App_Data/uploads";

    /// <summary>The request path the folder is served at, e.g. <c>/uploads</c>.</summary>
    public string RequestPath { get; set; } = "/uploads";

    /// <summary>The largest single upload the store accepts, in bytes.</summary>
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>The most images one product's gallery may hold.</summary>
    public int MaxPerProduct { get; set; } = 10;

    /// <summary>
    /// Extensions the store accepts. Raster formats only: SVG is deliberately absent because an SVG
    /// is a script document with an image content type, and the bytes are sniffed as well as the
    /// name, so renaming a payload does not get it in.
    /// </summary>
    public string[] AllowedExtensions { get; set; } = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
}
