using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebDS_Test.Services.Shopify;

/// <summary>Raised when the Storefront API returns GraphQL or transport errors.</summary>
public sealed class ShopifyStorefrontException : Exception
{
    public ShopifyStorefrontException(string message, IReadOnlyList<string>? errors = null)
        : base(errors is { Count: > 0 } ? $"{message} :: {string.Join(" | ", errors)}" : message)
    {
        Errors = errors ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> Errors { get; }

    /// <summary>True when the failure is worth retrying (throttling or a transient 5xx).</summary>
    public bool IsTransient { get; init; }
}

/// <summary>
/// Minimal GraphQL transport for the Storefront API. Exists as an interface so the catalogue
/// service can be tested against fixture responses without a shop, a token or a network.
/// </summary>
public interface IShopifyStorefrontClient
{
    /// <summary>Executes a query and deserializes the <c>data</c> payload into <typeparamref name="T"/>.</summary>
    Task<T> QueryAsync<T>(string query, object? variables = null, CancellationToken ct = default);
}

/// <summary>
/// HTTP implementation. Adds the storefront access token, checks the GraphQL <c>errors</c> array
/// before deserializing, and retries throttled or transient failures with a short backoff.
/// </summary>
public sealed class ShopifyStorefrontClient : IShopifyStorefrontClient
{
    private const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly ShopifyOptions _options;
    private readonly ILogger<ShopifyStorefrontClient> _logger;

    public ShopifyStorefrontClient(HttpClient http, IOptions<ShopifyOptions> options, ILogger<ShopifyStorefrontClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;

        _http.BaseAddress ??= new Uri(_options.BuildEndpoint());
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_options.StorefrontAccessToken))
        {
            _http.DefaultRequestHeaders.Remove("X-Shopify-Storefront-Access-Token");
            _http.DefaultRequestHeaders.Add("X-Shopify-Storefront-Access-Token", _options.StorefrontAccessToken);
        }

        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<T> QueryAsync<T>(string query, object? variables = null, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { query, variables }, JsonOptions);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            try
            {
                using var response = await _http.PostAsync(string.Empty, content, ct);

                // Throttling. The Storefront API is a calculated-cost bucket; back off and retry.
                if (response.StatusCode is HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt < MaxAttempts)
                    {
                        var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));
                        _logger.LogWarning(
                            "Storefront API returned {StatusCode}; retrying in {Delay}ms (attempt {Attempt}/{Max})",
                            (int)response.StatusCode, delay.TotalMilliseconds, attempt, MaxAttempts);

                        await Task.Delay(delay, ct);
                        continue;
                    }

                    throw new ShopifyStorefrontException(
                        $"Storefront API returned {(int)response.StatusCode} after {MaxAttempts} attempts.") { IsTransient = true };
                }

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new ShopifyStorefrontException(
                        $"Storefront API returned {(int)response.StatusCode}.", new[] { Truncate(body) });
                }

                var envelope = await response.Content.ReadFromJsonAsync<GraphQlEnvelope<T>>(JsonOptions, ct)
                               ?? throw new ShopifyStorefrontException("Storefront API returned an empty body.");

                if (envelope.Errors is { Count: > 0 })
                {
                    var messages = envelope.Errors
                        .Select(e => e.Message ?? "unspecified error")
                        .ToList();

                    // THROTTLED is Shopify's documented throttling error code.
                    var throttled = envelope.Errors.Any(e =>
                        string.Equals(e.Extensions?.Code, "THROTTLED", StringComparison.OrdinalIgnoreCase));

                    if (throttled && attempt < MaxAttempts)
                    {
                        var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                        _logger.LogWarning("Storefront API throttled the query; retrying in {Delay}ms", delay.TotalMilliseconds);
                        await Task.Delay(delay, ct);
                        continue;
                    }

                    throw new ShopifyStorefrontException("Storefront API reported errors.", messages) { IsTransient = throttled };
                }

                return envelope.Data
                       ?? throw new ShopifyStorefrontException("Storefront API returned a null data payload.");
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "Storefront API request failed; retrying (attempt {Attempt}/{Max})", attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1)), ct);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                _logger.LogWarning(ex, "Storefront API request timed out; retrying (attempt {Attempt}/{Max})", attempt, MaxAttempts);
            }
        }

        throw new ShopifyStorefrontException("Storefront API request failed after all retry attempts.") { IsTransient = true };
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";

    private sealed class GraphQlEnvelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
        [JsonPropertyName("errors")] public List<GraphQlError>? Errors { get; set; }
    }

    private sealed class GraphQlError
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("extensions")] public GraphQlErrorExtensions? Extensions { get; set; }
    }

    private sealed class GraphQlErrorExtensions
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
    }
}
