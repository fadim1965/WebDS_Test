namespace WebDS_Test.Services.Email;

/// <summary>
/// Settings for transactional email. Bound from the <c>Email</c> configuration section so a
/// deployment can switch transports (or turn mail off) without a rebuild.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// When false no message leaves the store. Messages are still written to the outbox with a
    /// status of <see cref="Domain.EmailStatus.Skipped"/>, so nothing is silently lost.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which <see cref="IEmailTransport"/> to use: <c>File</c> (write .eml files to
    /// <see cref="DropFolder"/>, the default and the only one that needs no mail server) or
    /// <c>Smtp</c>.
    /// </summary>
    public string Transport { get; set; } = "File";

    public string FromAddress { get; set; } = "no-reply@libanpost.example";

    public string FromName { get; set; } = "LibanPost eShop";

    public string? ReplyTo { get; set; }

    /// <summary>Folder the <c>File</c> transport drops rendered .eml files into.</summary>
    public string DropFolder { get; set; } = "App_Data/outbox";

    /// <summary>How many deliveries are attempted before a message is parked as failed.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>How often the dispatcher looks for work.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>How many messages one dispatch pass may handle.</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Base delay for the exponential backoff between attempts, in seconds.</summary>
    public int RetryBaseSeconds { get; set; } = 30;

    /// <summary>How long a password-reset link stays valid, in minutes.</summary>
    public int PasswordResetMinutes { get; set; } = 120;

    public SmtpOptions Smtp { get; set; } = new();
}

/// <summary>Connection details for the <c>Smtp</c> transport.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool UseStartTls { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>Whether the transport is usable with the current settings.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
