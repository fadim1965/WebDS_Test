using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WebDS_Test.Domain;
using WebDS_Test.Models;
using WebDS_Test.Services;

namespace WebDS_Test.Controllers;

/// <summary>
/// The shopper's side of shop conversations: the inbox, the thread, and the form that opens one
/// from a shop's page. The shop answers from <c>Areas/Vendor</c>; the two sides share nothing but
/// the thread itself, and every read here is scoped to the signed-in shopper — a thread that is
/// not theirs is indistinguishable from one that does not exist.
/// </summary>
[Authorize]
public class MessagesController : StoreControllerBase
{
    private readonly IConversationService _conversations;
    private readonly IShopService _shops;

    public MessagesController(
        ICatalogService catalog,
        IOptions<StoreSettings> settings,
        ICartService cart,
        IConversationService conversations,
        IShopService shops)
        : base(catalog, settings, cart)
    {
        _conversations = conversations;
        _shops = shops;
    }

    /// <summary>The shopper's inbox: every thread they opened, newest activity first.</summary>
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        await LoadCartBadgeAsync(ct);

        ViewData["Title"] = "Messages";
        return View(new ConversationInboxViewModel
        {
            Conversations = await _conversations.GetForCustomerAsync(userId, ct),
            Unread = await _conversations.CountUnreadForCustomerAsync(userId, ct)
        });
    }

    /// <summary>One thread with its messages. Opening it marks it read.</summary>
    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        var conversation = await _conversations.GetAsync(id, ct);
        if (conversation is null || conversation.UserId != userId) return NotFound();

        // Ownership is established first, so the read marker only ever moves on their own thread.
        await _conversations.MarkCustomerReadAsync(id, ct);
        await LoadCartBadgeAsync(ct);

        ViewData["Title"] = conversation.Subject;
        return View(new ConversationThreadViewModel
        {
            Conversation = conversation,
            IsCustomerView = true
        });
    }

    /// <summary>The blank form for contacting a shop, reached from its storefront page.</summary>
    [HttpGet]
    public async Task<IActionResult> Start(int shopId, int? orderId, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        var shop = await _shops.GetByIdAsync(shopId, ct);
        if (shop is null || shop.Status != ShopStatus.Approved) return NotFound();

        await LoadCartBadgeAsync(ct);

        ViewData["Title"] = $"Message {shop.Name}";
        ViewData["ShopName"] = shop.Name;
        ViewData["ShopSlug"] = shop.Slug;
        return View(new ConversationStartForm { ShopId = shopId, OrderId = orderId });
    }

    /// <summary>Opens the thread with the shopper's first message.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Messaging)]
    public async Task<IActionResult> Start(ConversationStartForm form, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        var shop = await _shops.GetByIdAsync(form.ShopId, ct);
        if (shop is null || shop.Status != ShopStatus.Approved) return NotFound();

        ViewData["ShopName"] = shop.Name;
        ViewData["ShopSlug"] = shop.Slug;

        if (!ModelState.IsValid)
        {
            Flash(FirstError(), success: false);
            ViewData["Title"] = $"Message {shop.Name}";
            return View(form);
        }

        // The address comes from the cookie the sign-in issued, not from the form: the shop must
        // be able to reply to the account that is actually writing.
        var email = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var name = User.Identity?.Name ?? "Shopper";

        var result = await _conversations.StartAsync(
            form.ShopId, form.OrderId, form.Subject, form.Body, userId, email, name, ct);

        Flash(result.Message, result.Success);

        if (!result.Success)
        {
            ViewData["Title"] = $"Message {shop.Name}";
            return View(form);
        }

        return RedirectToAction(nameof(Details), new { id = result.Conversation!.Id });
    }

    /// <summary>Appends the shopper's reply to the thread.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Messaging)]
    public async Task<IActionResult> Reply(ConversationReplyForm form, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        var conversation = await _conversations.GetAsync(form.ConversationId, ct);
        if (conversation is null || conversation.UserId != userId) return NotFound();

        if (!ModelState.IsValid)
        {
            Flash(FirstError(), success: false);
            return RedirectToAction(nameof(Details), new { id = form.ConversationId });
        }

        var result = await _conversations.ReplyAsync(
            form.ConversationId, ConversationAuthor.Customer,
            User.Identity?.Name ?? "Shopper", userId, form.Body, ct);

        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id = form.ConversationId });
    }

    /// <summary>Closes the thread from the shopper's side. There is no reopen in this build.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, CancellationToken ct)
    {
        var userId = CurrentUserId();
        if (userId is null) return Challenge();

        var conversation = await _conversations.GetAsync(id, ct);
        if (conversation is null || conversation.UserId != userId) return NotFound();

        var result = await _conversations.CloseAsync(id, ConversationAuthor.Customer, ct);
        Flash(result.Message, result.Success);
        return RedirectToAction(nameof(Details), new { id });
    }

    private string? CurrentUserId() =>
        User.Identity?.IsAuthenticated == true
            ? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            : null;

    /// <summary>The first validation message, so the flash banner explains what went wrong.</summary>
    private string FirstError() =>
        ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
        ?? "Please check the message and try again.";
}
