using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebDS_Test.Data;
using WebDS_Test.Domain;
using WebDS_Test.Services.Email;

namespace WebDS_Test.Tests;

/// <summary>
/// Small builders shared by the suites. Each context gets its own uniquely-named InMemory store, so
/// tests never see each other's rows and a test never has to be run in isolation.
/// </summary>
internal static class TestSupport
{
    internal static ApplicationDbContext NewDb() => NewDb(NewDbName());

    /// <summary>A name for an InMemory store, so two contexts can deliberately share one.</summary>
    internal static string NewDbName() => $"webds-{Guid.NewGuid():N}";

    /// <summary>
    /// A context over a named InMemory store. Passing the same name to two contexts makes them see
    /// the same rows while keeping separate change trackers — which is how a per-request context
    /// behaves in the running application, and what lets a test prove that a query did (or did not)
    /// load a navigation.
    /// </summary>
    internal static ApplicationDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    internal static IOptions<T> Options<T>(T value) where T : class => Microsoft.Extensions.Options.Options.Create(value);

    internal static EmailOptions EmailOptions(Action<EmailOptions>? configure = null)
    {
        var options = new EmailOptions();
        configure?.Invoke(options);
        return options;
    }

    internal static StoreSettings Store(Action<StoreSettings>? configure = null)
    {
        var store = new StoreSettings();
        configure?.Invoke(store);
        return store;
    }

    internal static BrandingOptions Brand() => new();

    internal static ApplicationUser User(
        string email = "shopper@example.com",
        string displayName = "Sam Shopper",
        bool active = true,
        string? passwordHash = null) =>
        new()
        {
            Email = email,
            NormalizedEmail = email.Trim().ToUpperInvariant(),
            DisplayName = displayName,
            IsActive = active,
            PasswordHash = passwordHash ?? "not-a-real-hash"
        };

    internal static Order Order(
        int id = 1,
        string number = "ORD-2026-0001",
        OrderStatus status = OrderStatus.Pending,
        string email = "shopper@example.com",
        string customerName = "Sam Shopper")
    {
        var order = new Order
        {
            Id = id,
            OrderNumber = number,
            Status = status,
            Email = email,
            CustomerName = customerName,
            ShopId = 1,
            ShippingName = customerName,
            ShippingLine1 = "12 Rue Verdun",
            ShippingCity = "Beirut",
            ShippingPostalCode = "1107",
            ShippingCountry = "Lebanon",
            Subtotal = 49.00m,
            ShippingTotal = 4.50m,
            TaxTotal = 5.39m,
            GrandTotal = 58.89m,
            PaymentMethod = PaymentMethod.CashOnDelivery,
            PlacedUtc = DateTime.UtcNow
        };

        order.Lines.Add(new OrderLine
        {
            ProductName = "EMS Priority Envelope",
            VariantName = "Pack of 10",
            Sku = "EMS-10",
            Quantity = 2,
            UnitPrice = 24.50m,
            LineTotal = 49.00m
        });

        return order;
    }
}

/// <summary>A transport that records what it was asked to send, and can be told to fail.</summary>
internal sealed class RecordingTransport : IEmailTransport
{
    private readonly Queue<Exception> _failures = new();

    public string Name => "recording";

    public List<EmailEnvelope> Sent { get; } = new();

    public void FailNext(params Exception[] exceptions)
    {
        foreach (var exception in exceptions)
        {
            _failures.Enqueue(exception);
        }
    }

    public Task SendAsync(EmailEnvelope envelope, CancellationToken ct = default)
    {
        if (_failures.Count > 0)
        {
            throw _failures.Dequeue();
        }

        Sent.Add(envelope);
        return Task.CompletedTask;
    }
}
