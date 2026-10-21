using System.Net;
using System.Text;

namespace WebDS_Test.Services.Email;

/// <summary>
/// The default transport: it writes each message to disk as a real RFC 822 <c>.eml</c> file (plus a
/// browser-openable <c>.html</c> copy) instead of talking to a mail server.
/// </summary>
/// <remarks>
/// <para>
/// This is the transport that makes transactional email work on a machine with no SMTP server —
/// which is every development machine, and this one in particular. The files it produces are not a
/// stub: they have proper MIME headers and both a plain-text and an HTML part, so they open
/// correctly in a mail client and are accepted by any tool that ingests <c>.eml</c> (for example a
/// staging mail server that picks files up from a directory).
/// </para>
/// <para>
/// The MIME is assembled by hand rather than with <c>System.Net.Mail</c> because the BCL
/// <c>SmtpClient</c> family is obsolete (<c>SYSLIB0014</c>) and the modern alternative, MailKit, is
/// not in the offline NuGet cache. Assembling a small multipart/alternative body is well within
/// reach and keeps the build free of suppressions.
/// </para>
/// </remarks>
public sealed class FileEmailTransport : IEmailTransport
{
    private readonly EmailOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<FileEmailTransport> _logger;

    public FileEmailTransport(
        IOptions<EmailOptions> options,
        IHostEnvironment environment,
        ILogger<FileEmailTransport> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public string Name => "file";

    public Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var folder = ResolveFolder();
        Directory.CreateDirectory(folder);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var stem = $"{stamp}-{envelope.ToAddress.Split('@')[0]}-{Slug(envelope.Subject)}";
        stem = stem.Length > 80 ? stem[..80] : stem;

        var emlPath = Unique(folder, stem, ".eml");
        File.WriteAllText(emlPath, BuildMime(envelope), new UTF8Encoding(false));

        // A second, plain copy with no MIME wrapper, so the message can simply be opened in a
        // browser while developing. The .eml alongside it is the real artifact.
        var htmlPath = Path.ChangeExtension(emlPath, ".html");
        File.WriteAllText(htmlPath, envelope.HtmlBody, new UTF8Encoding(false));

        _logger.LogInformation(
            "Email transport 'file' wrote {File} for {To}", Path.GetFileName(emlPath), envelope.ToAddress);

        return Task.CompletedTask;
    }

    private string ResolveFolder() =>
        Path.IsPathRooted(_options.DropFolder)
            ? _options.DropFolder
            : Path.GetFullPath(_options.DropFolder, _environment.ContentRootPath);

    private static string Unique(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);
        var counter = 1;

        while (File.Exists(path))
        {
            path = Path.Combine(folder, $"{stem}-{counter++}{extension}");
        }

        return path;
    }

    private static string BuildMime(EmailEnvelope envelope)
    {
        var boundary = "=_WebDS_" + Guid.NewGuid().ToString("N");

        var builder = new StringBuilder();
        builder.Append("MIME-Version: 1.0\r\n");
        builder.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r")).Append("\r\n");
        builder.Append("From: ").Append(FormatAddress(envelope.FromAddress, envelope.FromName)).Append("\r\n");
        builder.Append("To: ").Append(FormatAddress(envelope.ToAddress, envelope.ToName)).Append("\r\n");

        if (!string.IsNullOrWhiteSpace(envelope.ReplyTo))
        {
            builder.Append("Reply-To: ").Append(FormatAddress(envelope.ReplyTo!, null)).Append("\r\n");
        }

        builder.Append("Subject: ").Append(EncodeWord(envelope.Subject)).Append("\r\n");
        builder.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n");
        builder.Append("\r\n");

        AppendPart(builder, boundary, "text/plain", envelope.TextBody);
        AppendPart(builder, boundary, "text/html", envelope.HtmlBody);

        builder.Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }

    private static void AppendPart(StringBuilder builder, string boundary, string contentType, string body)
    {
        builder.Append("--").Append(boundary).Append("\r\n");
        builder.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        builder.Append("Content-Transfer-Encoding: base64\r\n");
        builder.Append("\r\n");
        builder.Append(WrapBase64(Convert.ToBase64String(Encoding.UTF8.GetBytes(body))));
        builder.Append("\r\n");
    }

    private static string WrapBase64(string value)
    {
        const int width = 76;
        if (value.Length <= width)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + value.Length / width * 2);
        for (var i = 0; i < value.Length; i += width)
        {
            builder.Append(value, i, Math.Min(width, value.Length - i)).Append("\r\n");
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static string FormatAddress(string address, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return address.Trim();
        }

        // A display name with anything outside printable ASCII is sent as an RFC 2047 encoded word.
        var encoded = name.Any(c => c > 127 || c == '"' || c == '\\')
            ? EncodeWord(name!)
            : "\"" + name!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        return $"{encoded} <{address.Trim()}>";
    }

    private static string EncodeWord(string value)
    {
        if (value.All(c => c is >= ' ' and <= '~'))
        {
            return value;
        }

        return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";
    }

    private static string Slug(string value)
    {
        var chars = value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();

        var slug = new string(chars);

        while (slug.Contains("--"))
        {
            slug = slug.Replace("--", "-");
        }

        return slug.Trim('-');
    }
}
