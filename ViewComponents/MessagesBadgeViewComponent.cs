using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WebDS_Test.Services;

namespace WebDS_Test.ViewComponents;

/// <summary>
/// The unread pill on the header's Messages link. A view component for the same reason the cart
/// pill is one: the count is live rather than stale in a cached layout, and the layout needs
/// neither the count nor the inbox in its model.
/// </summary>
/// <remarks>
/// The badge counts the signed-in shopper's threads, and renders nothing at all for a guest or a
/// shopper with nothing waiting — so the nav reads the same as before messaging existed.
/// </remarks>
public sealed class MessagesBadgeViewComponent : ViewComponent
{
    private readonly IConversationService _conversations;

    public MessagesBadgeViewComponent(IConversationService conversations) =>
        _conversations = conversations;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var userId = HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return View(new MessagesBadgeModel(0));
        }

        return View(new MessagesBadgeModel(await _conversations.CountUnreadForCustomerAsync(userId)));
    }
}

public sealed record MessagesBadgeModel(int Unread);
