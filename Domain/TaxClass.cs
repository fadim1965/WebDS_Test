namespace WebDS_Test.Domain;

/// <summary>
/// A named tax rate. A product points at one; a product with no class falls back to
/// <c>Store:TaxRate</c>, so the flat rate every order used before is still the default.
/// </summary>
/// <remarks>
/// Rates are stored as a fraction, not a percentage, for the same reason <see cref="StoreSettings.TaxRate"/>
/// is: 0.11 means eleven percent, and there is no place for someone to type "11" by mistake.
/// </remarks>
public sealed class TaxClass
{
    public int Id { get; set; }

    /// <summary>Staff-facing label, e.g. "Standard rate" or "Zero rated".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Fractional rate: 0.11 is 11%.</summary>
    public decimal Rate { get; set; }

    /// <summary>Inactive classes stay on old orders but are not offered for new products.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>A product may be exempt (VAT-registered businesses, resale certificates).</summary>
    public bool IsExempt { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
