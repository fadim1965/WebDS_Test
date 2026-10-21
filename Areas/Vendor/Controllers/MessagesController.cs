using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Areas.Vendor.Controllers;

/// <summary>
/// The shop's inbox: the questions shoppers opened from the storefront, answered from here.
/// Nothing moves money and nothing changes an order — a conversation is correspondence, and it is
/// audited the way the other vendor actions are so the trail shows who answered what.
/// </summary>
/// <remarks>
/// Every query is filtered by the resolved shop, so a vendor sees their own threads and nothing
/// else — a conversation belonging to another shop is a 404, not a redaction.
/// </remarks>
[Area("Vendor")]
[Authorize(Policy = "VendorOnly")]
public class MessagesController : Controller
{
    private readonly IConversationService _conversations;
    private readonly IShopService _shops;
    private readonly IAuditTrail _audit;

    public MessagesController(
        IConversationService conversations,
        IShopService shops,
        IAuditTrail audit)
    {
        _conversations = conversations;
        _shops = shops;
        _audit = audit;
    }

    private void Flash(string message, bool success = true)
    {
        TempData["Message"] = message;
        TempData["MessageType"] = success ? "success" : "danger";
    }

    private async Task<Shop?> ResolveShopAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return null;
        return await _shops.GetPrimaryShopForUserAsync(userId, ct);
    }

    /// <summary>The shop's threads, newest activity first.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(bool? open, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var rows = await _conversations.GetForShopAsync(shop.Id, open, ct);

        ViewData["Title"] = "Messages";
        ViewData["ShopName"] = shop.Name;
        ViewData["OpenOnly"] = open;
        ViewData["Unread"] = await _conversations.CountUnreadForShopAsync(shop.Id, ct);
        return View(rows);
    }

    /// <summary>One thread with its messages. Opening it marks it read for the shop.</summary>
    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var conversation = await _conversations.GetAsync(id, ct);

        // Belonging to another shop is indistinguishable from not existing.
        if (conversation is null || conversation.ShopId != shop.Id) return NotFound();

        await _conversations.MarkVendorReadAsync(id, ct);

        ViewData["Title"] = $"Message {conversation.Reference}";
        ViewData["ShopName"] = shop.Name;
        return View(new ConversationThreadViewModel
        {
            Conversation = conversation,
            IsCustomerView = false
        });
    }

    /// <summary>Appends the shop's reply and mails the shopper.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Messaging)]
    public async Task<IActionResult> Reply(ConversationReplyForm form, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var conversation = await _conversations.GetAsync(form.ConversationId, ct);
        if (conversation is null || conversation.ShopId != shop.Id) return NotFound();

        if (!ModelState.IsValid)
        {
            Flash("Write a message of up to 4,000 characters.", success: false);
            return RedirectToAction(nameof(Details), new { id = form.ConversationId });
        }

        var actor = User.Identity?.Name ?? "vendor";
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var result = await _conversations.ReplyAsync(
            form.ConversationId, ConversationAuthor.Vendor, actor, userId, form.Body, ct);

        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(actor, "conversation.replied", "Conversation", form.ConversationId,
                $"Replied to {conversation.Reference} (\"{conversation.Subject}\") at shop {shop.Name}.");
        }

        return RedirectToAction(nameof(Details), new { id = form.ConversationId });
    }

    /// <summary>Closes the thread from the shop's side. There is no reopen in this build.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, CancellationToken ct)
    {
        var shop = await ResolveShopAsync(ct);
        if (shop is null) return RedirectToAction("Register", "Onboarding", new { area = "Vendor" });

        var conversation = await _conversations.GetAsync(id, ct);
        if (conversation is null || conversation.ShopId != shop.Id) return NotFound();

        var actor = User.Identity?.Name ?? "vendor";
        var result = await _conversations.CloseAsync(id, ConversationAuthor.Vendor, ct);
        Flash(result.Message, result.Success);

        if (result.Success)
        {
            await _audit.RecordAsync(actor, "conversation.closed", "Conversation", id,
                $"Closed {conversation.Reference} (\"{conversation.Subject}\") at shop {shop.Name}.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }
}
