namespace WebDS_Test.Services.Email;

/// <summary>
/// Drains the transactional email outbox in the background.
/// </summary>
/// <remarks>
/// <para>
/// The dispatcher is what keeps a mail server off the request path: a checkout returns as soon as
/// the confirmation is queued, and this loop delivers it a moment later. Each pass runs in its own
/// DI scope, so it has its own <see cref="ApplicationDbContext"/> and never shares one with a live
/// request.
/// </para>
/// <para>
/// It is deliberately hard to kill: every fault is logged and swallowed, and the loop waits for the
/// next tick. A transient database or transport problem must never take the web host down, and a
/// message that cannot be sent stays in the outbox for the next attempt (or for an administrator to
/// retry from the Emails page).
/// </para>
/// </remarks>
public sealed class EmailDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailDispatcher> _logger;

    public EmailDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<EmailOptions> options,
        ILogger<EmailDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Transactional email is disabled (Email:Enabled = false); the dispatcher is idle.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 1, 300));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Email dispatcher started: transport '{Transport}', every {Seconds}s, up to {Batch} per pass",
            _options.Transport, interval.TotalSeconds, _options.BatchSize);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IEmailService>();
                await service.ProcessQueueAsync(_options.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad pass must not end the loop.
                _logger.LogError(ex, "Email dispatch pass failed; retrying on the next tick.");
            }
        }
    }

    private async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
