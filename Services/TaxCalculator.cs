using WebDS_Test.Domain;

namespace WebDS_Test.Services;

/// <summary>
/// Works out the tax on an order. Kept separate from <see cref="IOrderService"/> so the arithmetic can
/// be tested without a database, and so the checkout preview and the order that is actually placed
/// cannot drift apart — both call this.
/// </summary>
public interface ITaxCalculator
{
    /// <summary>
    /// Tax for one line. Rounding happens per line, not once at the end, because that is how a real
    /// invoice is produced: each line carries its own rounded tax figure.
    /// </summary>
    decimal TaxForLine(OrderLine line, decimal storeDefaultRate, MidpointRounding rounding);

    /// <summary>Tax for a whole basket, as the sum of its lines.</summary>
    decimal TaxForLines(IEnumerable<OrderLine> lines, decimal storeDefaultRate, MidpointRounding rounding);

    /// <summary>
    /// Tax for a basket whose lines carry different rates, with an order-level discount apportioned
    /// across them. This is what checkout uses.
    /// </summary>
    decimal TaxForMixedBasket(
        IReadOnlyList<TaxableLine> lines, decimal discount, decimal storeDefaultRate, MidpointRounding rounding);

    /// <summary>The rate that applies to a line, after exemption and inactive-class rules.</summary>
    decimal RateFor(TaxClass? taxClass, decimal storeDefaultRate);
}

/// <summary>A line as the calculator needs it: its value, and the tax class attached to it.</summary>
public sealed record TaxableLine(decimal GrossTotal, TaxClass? TaxClass);

public sealed class TaxCalculator : ITaxCalculator
{
    public decimal RateFor(TaxClass? taxClass, decimal storeDefaultRate) => taxClass switch
    {
        // Exempt wins over everything: a VAT-registered buyer pays no tax whatever the rate says.
        { IsExempt: true } => 0m,
        { IsActive: true } => taxClass.Rate,
        // No class, or one deactivated since: the store-wide rate, which is what every order did
        // before tax classes existed.
        _ => storeDefaultRate
    };

    public decimal TaxForLine(OrderLine line, decimal storeDefaultRate, MidpointRounding rounding)
    {
        var rate = RateFor(line.ProductVariant?.Product?.TaxClass, storeDefaultRate);
        return Math.Round(line.LineTotal * rate, 2, rounding);
    }

    public decimal TaxForLines(IEnumerable<OrderLine> lines, decimal storeDefaultRate, MidpointRounding rounding) =>
        lines.Sum(l => TaxForLine(l, storeDefaultRate, rounding));

    public decimal TaxForMixedBasket(
        IReadOnlyList<TaxableLine> lines, decimal discount, decimal storeDefaultRate, MidpointRounding rounding)
    {
        if (lines.Count == 0)
        {
            return 0m;
        }

        var goodsSubtotal = lines.Sum(l => l.GrossTotal);
        if (goodsSubtotal <= 0m)
        {
            return 0m;
        }

        var shares = ShareDiscount(lines.Select(l => l.GrossTotal).ToList(), discount, rounding);
        var total = 0m;

        for (var i = 0; i < lines.Count; i++)
        {
            var taxable = Math.Round(lines[i].GrossTotal - shares[i], 2, rounding);
            total += Math.Round(taxable * RateFor(lines[i].TaxClass, storeDefaultRate), 2, rounding);
        }

        return total;
    }

    /// <summary>
    /// Splits a discount across lines in proportion to their value, without losing or inventing a
    /// cent: each share is rounded, and the last line absorbs the remainder so the parts always sum
    /// back to the whole. A discount that does not divide evenly must still total exactly what the
    /// shopper was given.
    /// </summary>
    public static IReadOnlyList<decimal> ShareDiscount(
        IReadOnlyList<decimal> lineTotals, decimal discount, MidpointRounding rounding)
    {
        var result = new decimal[lineTotals.Count];
        if (lineTotals.Count == 0 || discount <= 0m)
        {
            return result;
        }

        var goodsSubtotal = lineTotals.Sum();
        if (goodsSubtotal <= 0m)
        {
            return result;
        }

        var remaining = Math.Min(discount, goodsSubtotal);

        for (var i = 0; i < lineTotals.Count; i++)
        {
            var share = i == lineTotals.Count - 1
                ? remaining
                : Math.Round(remaining * (lineTotals[i] / goodsSubtotal), 2, rounding);

            // A share can never exceed the line it belongs to or the discount left to give.
            share = Math.Min(Math.Max(share, 0m), Math.Min(remaining, Math.Max(lineTotals[i], 0m)));
            result[i] = share;
            remaining -= share;
        }

        return result;
    }
}
