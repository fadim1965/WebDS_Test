namespace WebDS_Test.Services.Email;

// System.Net.Mail is obsolete as of .NET 6 (SYSLIB0014) because SmtpClient does not support modern
// authentication. It is used here deliberately and only here: MailKit, the recommended
// replacement, is not in this machine's offline NuGet cache, and the alternative — hand-rolling an
// SMTP conversation over TcpClient — is more code and less reliable than the BCL client. The
// suppression is scoped to this one file so every other warning stays visible.
#pragma warning disable SYSLIB0014

/// <summary>
/// Delivers over SMTP. Selected with <c>Email:Transport = "Smtp"</c> once
/// <c>Email:Smtp:Host</c> is configured; the default transport is <see cref="FileEmailTransport"/>.
/// </summary>
public sealed class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(IOptions<EmailOptions> options, ILogger<SmtpEmailTransport> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Name => "smtp";

    public async Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default)
    {
        if (!_options.Smtp.IsConfigured)
        {
            throw new InvalidOperationException(
                "Email:Smtp:Host is not configured, so the SMTP transport cannot send.");
        }

        using var message = new System.Net.Mail.MailMessage
        {
            From = new System.Net.Mail.MailAddress(envelope.FromAddress, envelope.FromName),
            Subject = envelope.Subject,
            Body = envelope.HtmlBody,
            IsBodyHtml = true
        };

        message.To.Add(string.IsNullOrWhiteSpace(envelope.ToName)
            ? new System.Net.Mail.MailAddress(envelope.ToAddress)
            : new System.Net.Mail.MailAddress(envelope.ToAddress, envelope.ToName));

        // The client sends the HTML body but falls back to the plain-text alternative when it
        // cannot render HTML — the same two parts the file transport writes.
        message.AlternateViews.Add(
            System.Net.Mail.AlternateView.CreateAlternateViewFromString(
                envelope.TextBody, null, "text/plain"));

        if (!string.IsNullOrWhiteSpace(envelope.ReplyTo))
        {
            message.ReplyToList.Add(new System.Net.Mail.MailAddress(envelope.ReplyTo!));
        }

        using var client = new System.Net.Mail.SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
        {
            EnableSsl = _options.Smtp.UseStartTls
        };

        if (!string.IsNullOrWhiteSpace(_options.Smtp.UserName))
        {
            client.Credentials = new System.Net.NetworkCredential(_options.Smtp.UserName, _options.Smtp.Password);
        }

        await client.SendMailAsync(message, ct);

        _logger.LogInformation(
            "Email transport 'smtp' delivered {Subject} to {To} via {Host}",
            envelope.Subject, envelope.ToAddress, _options.Smtp.Host);
    }
}

#pragma warning restore SYSLIB0014
