using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// Status of a carrier remittance document: a batch of cash the carrier says it paid over.
/// </summary>
public enum RemittanceStatus
{
    /// <summary>Being prepared; not yet received from the carrier.</summary>
    Draft = 0,
    /// <summary>Carrier statement recorded, awaiting line-by-line reconciliation.</summary>
    Received = 1,
    /// <summary>Every line reconciled; variance known and (if any) recorded.</summary>
    Reconciled = 2,
    /// <summary>Variance beyond tolerance; needs investigation before it can be closed.</summary>
    Disputed = 3,
    /// <summary>Variance written off or recovered; no further action.</summary>
    Closed = 4
}

public static class RemittanceStatusExtensions
{
    public static string ToDisplay(this RemittanceStatus s) => s switch
    {
        RemittanceStatus.Draft => "Draft",
        RemittanceStatus.Received => "Received",
        RemittanceStatus.Reconciled => "Reconciled",
        RemittanceStatus.Disputed => "Disputed",
        RemittanceStatus.Closed => "Closed",
        _ => s.ToString()
    };

    public static string ToCssClass(this RemittanceStatus s) => s switch
    {
        RemittanceStatus.Draft => "text-bg-secondary",
        RemittanceStatus.Received => "text-bg-info",
        RemittanceStatus.Reconciled => "text-bg-success",
        RemittanceStatus.Disputed => "text-bg-danger",
        RemittanceStatus.Closed => "text-bg-dark",
        _ => "text-bg-secondary"
    };
}

/// <summary>
/// Outcome of matching one order's expected cash against what the carrier says it paid.
/// </summary>
public enum RemittanceLineStatus
{
    /// <summary>Cash matches what was expected (within tolerance).</summary>
    Matched = 0,
    /// <summary>Carrier paid less than expected: a shortfall, owed by the carrier.</summary>
    Short = 1,
    /// <summary>Carrier paid more than expected: an overpayment, to be returned or credited.</summary>
    Over = 2,
    /// <summary>No delivered order matches this line.</summary>
    Unmatched = 3,
    /// <summary>The order was returned/RTS, so no cash was expected; any cash paid is recovered.</summary>
    Returned = 4,
    /// <summary>Variance written off.</summary>
    WrittenOff = 5
}

public static class RemittanceLineStatusExtensions
{
    public static string ToDisplay(this RemittanceLineStatus s) => s switch
    {
        RemittanceLineStatus.Matched => "Matched",
        RemittanceLineStatus.Short => "Short",
        RemittanceLineStatus.Over => "Over",
        RemittanceLineStatus.Unmatched => "Unmatched",
        RemittanceLineStatus.Returned => "Returned",
        RemittanceLineStatus.WrittenOff => "Written off",
        _ => s.ToString()
    };

    public static string ToCssClass(this RemittanceLineStatus s) => s switch
    {
        RemittanceLineStatus.Matched => "text-bg-success",
        RemittanceLineStatus.Short => "text-bg-danger",
        RemittanceLineStatus.Over => "text-bg-warning",
        RemittanceLineStatus.Unmatched => "text-bg-secondary",
        RemittanceLineStatus.Returned => "text-bg-info",
        RemittanceLineStatus.WrittenOff => "text-bg-dark",
        _ => "text-bg-secondary"
    };
}

/// <summary>
/// A batch of cash the carrier says it remitted against delivered cash-on-delivery orders.
/// The marketplace reconciles the statement line by line against what it expected to collect,
/// so a carrier shortfall or an overpayment surfaces as a numbered document rather than a
/// note in a spreadsheet.
/// </summary>
public class CarrierRemittance
{
    public int Id { get; set; }

    /// <summary>Carrier key, e.g. <c>libanpost</c> or <c>simulator</c>.</summary>
    [Required, StringLength(40)]
    public string Carrier { get; set; } = string.Empty;

    /// <summary>The carrier's own batch / statement number.</summary>
    [Required, StringLength(80)]
    public string Reference { get; set; } = string.Empty;

    [StringLength(120)]
    public string? BankReference { get; set; }

    /// <summary>When the carrier says the money was paid.</summary>
    public DateTime RemittanceDateUtc { get; set; }

    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;

    [StringLength(256)]
    public string? ReceivedBy { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    public RemittanceStatus Status { get; set; } = RemittanceStatus.Draft;

    /// <summary>What we expected to collect, summed from the matched orders.</summary>
    public decimal ExpectedAmount { get; set; }

    /// <summary>What the carrier says it paid.</summary>
    public decimal ActualAmount { get; set; }

    /// <summary>Actual minus expected. Negative means the carrier underpaid.</summary>
    public decimal Variance { get; set; }

    /// <summary>Per-line tolerance within which a variance counts as matched.</summary>
    public decimal Tolerance { get; set; } = 0.01m;

    public DateTime? ReconciledUtc { get; set; }

    [StringLength(256)]
    public string? ReconciledBy { get; set; }

    public DateTime? ClosedUtc { get; set; }

    [StringLength(256)]
    public string? ClosedBy { get; set; }

    public ICollection<CarrierRemittanceLine> Lines { get; set; } = new List<CarrierRemittanceLine>();
}

/// <summary>One order's expected cash against what the carrier paid for it.</summary>
public class CarrierRemittanceLine
{
    public int Id { get; set; }

    public int CarrierRemittanceId { get; set; }
    public CarrierRemittance? CarrierRemittance { get; set; }

    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    [StringLength(120)]
    public string? TrackingNumber { get; set; }

    /// <summary>Cash the order should have produced (the order's CodAmount).</summary>
    public decimal ExpectedAmount { get; set; }

    /// <summary>Cash the carrier says it paid for this order.</summary>
    public decimal ActualAmount { get; set; }

    /// <summary>Actual minus expected.</summary>
    public decimal Variance { get; set; }

    public RemittanceLineStatus Status { get; set; } = RemittanceLineStatus.Matched;

    [StringLength(300)]
    public string? Note { get; set; }

    /// <summary>When the vendor ledger was adjusted for this line, if it was.</summary>
    public DateTime? SettledUtc { get; set; }
}
