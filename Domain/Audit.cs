using System.ComponentModel.DataAnnotations;

namespace WebDS_Test.Domain;

/// <summary>
/// One recorded administrative action: who did what to which record, and when.
/// </summary>
/// <remarks>
/// <para>
/// A flat, append-only table with loose references — the same shape the email outbox uses. The
/// actor is stored as the identity string shown in the admin area rather than a foreign key, so
/// deleting an account never rewrites history; <see cref="EntityType"/> and <see cref="EntityId"/>
/// name the record acted upon without constraining it, because a log that dies with its row is a
/// log that cannot explain anything older than the row.
/// </para>
/// <para>
/// Entries are written best-effort alongside the action they describe: a failing audit write is
/// logged and swallowed rather than allowed to undo an already-committed operation. Every writer
/// passes a short human-readable summary so the trail reads like a history book, not a diff.
/// </para>
/// </remarks>
public class AuditLog
{
    public int Id { get; set; }

    /// <summary>Who performed the action — the admin area's identity string, e.g. an email.</summary>
    [Required, StringLength(256)]
    public string Actor { get; set; } = string.Empty;

    /// <summary>A stable dotted key, e.g. <c>shop.approved</c>, <c>order.refunded</c>.</summary>
    [Required, StringLength(80)]
    public string Action { get; set; } = string.Empty;

    /// <summary>The kind of record acted on, e.g. <c>Shop</c>, <c>Order</c>, <c>VendorPayout</c>.</summary>
    [Required, StringLength(40)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The record's id, when the action targeted one.</summary>
    public int? EntityId { get; set; }

    /// <summary>Short human-readable description of what happened.</summary>
    [StringLength(500)]
    public string? Summary { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
