namespace WebDS_Test.Services.Images;

/// <summary>
/// Recognises an image from its leading bytes. Extension checks are trivially spoofed, so the store
/// sniffs the content as well and requires the two to agree.
/// </summary>
public static class ImageFormats
{
    /// <summary>The format implied by a file's magic bytes, or <c>null</c> if it is not a known image.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (Starts(bytes, 0xFF, 0xD8, 0xFF))
        {
            return "jpeg";
        }

        if (Starts(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
        {
            return "png";
        }

        // "GIF8" — covers both GIF87a and GIF89a.
        if (Starts(bytes, 0x47, 0x49, 0x46, 0x38))
        {
            return "gif";
        }

        // "RIFF" .... "WEBP"
        if (bytes.Length >= 12 && Starts(bytes, 0x52, 0x49, 0x46, 0x46) &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "webp";
        }

        return null;
    }

    /// <summary>Whether an extension is a legal name for the detected format.</summary>
    public static bool MatchesExtension(string format, string extension) => (format, extension.ToLowerInvariant()) switch
    {
        ("jpeg", ".jpg") or ("jpeg", ".jpeg") => true,
        ("png", ".png") => true,
        ("gif", ".gif") => true,
        ("webp", ".webp") => true,
        _ => false
    };

    private static bool Starts(ReadOnlySpan<byte> bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
