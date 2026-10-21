namespace WebDS_Test.Services.Email;

/// <summary>
/// One fully-rendered message, ready to hand to a transport. Deliberately a plain value: a
/// transport never needs to know why a message exists, only how to send it.
/// </summary>
public sealed record EmailEnvelope(
    string FromAddress,
    string FromName,
    string? ReplyTo,
    string ToAddress,
    string? ToName,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>
/// A way of delivering a rendered message. The outbox owns retry and auditing; a transport just
/// makes one attempt and throws if it cannot.
/// </summary>
public interface IEmailTransport
{
    /// <summary>A short name recorded in logs ("file", "smtp").</summary>
    string Name { get; }

    Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default);
}
