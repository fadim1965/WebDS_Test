using WebDS_Test.Services.Shipping;

namespace WebDS_Test.Services;

/// <summary>
/// Holds the configured carriers and decides which one is used by default.
/// </summary>
/// <remarks>
/// A registry rather than a single injected carrier, because the marketplace needs at least two
/// alive at once: the simulator for exercising fulfilment, and the real carrier for production. The
/// default is chosen by <c>Shipping:Provider</c>.
/// </remarks>
public sealed class CarrierRegistry : ICarrierRegistry
{
    private readonly IReadOnlyList<IShippingCarrier> _carriers;
    private readonly ILogger<CarrierRegistry> _logger;

    public CarrierRegistry(IEnumerable<IShippingCarrier> carriers, IOptions<ShippingCarrierOptions> options, ILogger<CarrierRegistry> logger)
    {
        _carriers = carriers.ToList();
        _logger = logger;

        var requested = options.Value.Provider;

        // Fall back to the simulator rather than throwing: an unknown provider name should degrade
        // to a working fulfilment path, and the warning makes the misconfiguration visible.
        Default = _carriers.FirstOrDefault(c => c.Key.Equals(requested, StringComparison.OrdinalIgnoreCase))
                  ?? _carriers.FirstOrDefault(c => c.Key == "simulator")
                  ?? _carriers.First();

        if (!Default.Key.Equals(requested, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Configured carrier '{Requested}' is not registered; using '{Actual}' instead",
                requested, Default.Key);
        }
    }

    public IShippingCarrier Default { get; }

    public IShippingCarrier? Resolve(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? Default
            : _carriers.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<IShippingCarrier> All => _carriers;
}
