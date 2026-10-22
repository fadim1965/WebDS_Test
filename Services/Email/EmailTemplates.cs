using System.Globalization;
using System.Net;

namespace WebDS_Test.Services.Email;

/// <summary>A rendered message: what a <see cref="EmailMessage"/> row is built from.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// Builds the store's transactional messages. Pure and static on purpose: a template takes the
/// data it needs and returns a <see cref="RenderedEmail"/>, which makes every message testable
/// without a database, a request or a mail server.
/// </summary>
public static class EmailTemplates
{
    public static RenderedEmail Welcome(ApplicationUser user, StoreSettings store, BrandingOptions brand)
    {
        var name = Display(user.DisplayName);
        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(name)},</p>
            <p style="margin:0 0 12px">
              Welcome to {Escape(store.Name)}. Your account is ready — you can track orders,
              keep a wishlist and save delivery addresses.
            </p>
            <p style="margin:0 0 12px">
              When you place an order we will email a confirmation here, and message you again the
              moment it is handed to the carrier.
            </p>
            """;

        var html = Layout(store, brand, "Welcome aboard", body);
        var text = $"""
            Hi {name},

            Welcome to {store.Name}. Your account is ready.

            When you place an order we will email a confirmation, and message you again when it ships.

            {store.Name}
            """;

        return new RenderedEmail($"Welcome to {store.Name}", html, text);
    }

    public static RenderedEmail OrderConfirmation(Order order, StoreSettings store, BrandingOptions brand)
    {
        // The confirmation is queued at placement, which for a card order is before the provider has
        // answered — so it must not claim the payment has gone through.
        var payment = order.PaymentMethod switch
        {
            PaymentMethod.CashOnDelivery =>
                "You chose cash on delivery, so nothing has been charged yet — you pay the courier when it arrives.",
            PaymentMethod.Card =>
                "Your order is reserved but not yet paid: finish the payment on our secure payment page to confirm it.",
            _ =>
                "You chose to pay at a LibanPost counter, so nothing has been charged yet."
        };

        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(order.CustomerName))},</p>
            <p style="margin:0 0 12px">
              Thank you — we have your order <strong>{Escape(order.OrderNumber)}</strong> and it is
              being prepared.
            </p>
            {Lines(order, store, brand)}
            <p style="margin:18px 0 4px;font-weight:600">Delivering to</p>
            <p style="margin:0 0 12px;color:{brand.InkMuted}">{Escape(order.ShippingSummary)}</p>
            <p style="margin:16px 0 0;color:{brand.InkSubtle};font-size:13px">{Escape(payment)}</p>
            """;

        var html = Layout(store, brand, $"Order {order.OrderNumber} confirmed", body);

        var text = $"""
            Hi {Display(order.CustomerName)},

            Thank you — we have your order {order.OrderNumber} and it is being prepared.

            {TextLines(order, store)}

            Total: {Money(order.GrandTotal, store)}

            Shipping to: {order.ShippingSummary}

            {store.Name}
            """;

        return new RenderedEmail($"Order {order.OrderNumber} confirmed", html, text);
    }

    public static RenderedEmail OrderShipped(Order order, StoreSettings store, BrandingOptions brand)
    {
        var tracking = string.IsNullOrWhiteSpace(order.TrackingNumber)
            ? "<p style=\"margin:0 0 12px\">Your parcel is on its way.</p>"
            : $"""
               <p style="margin:0 0 12px">
                 Your parcel is on its way. Tracking number
                 <strong>{Escape(order.TrackingNumber!)}</strong>
                 {(string.IsNullOrWhiteSpace(order.Carrier) ? string.Empty : $"({Escape(order.Carrier!)})")}.
               </p>
               """;

        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(order.CustomerName))},</p>
            {tracking}
            {Lines(order, store, brand)}
            <p style="margin:16px 0 0;color:{brand.InkSubtle};font-size:13px">
              Delivering to {Escape(order.ShippingSummary)}
            </p>
            """;

        var html = Layout(store, brand, $"Order {order.OrderNumber} has shipped", body);

        var text = $"""
            Hi {Display(order.CustomerName)},

            Your order {order.OrderNumber} has shipped.
            {(string.IsNullOrWhiteSpace(order.TrackingNumber) ? string.Empty : $"Tracking: {order.TrackingNumber} {order.Carrier}".Trim())}

            {TextLines(order, store)}

            Delivering to: {order.ShippingSummary}

            {store.Name}
            """;

        return new RenderedEmail($"Order {order.OrderNumber} has shipped", html, text);
    }

    public static RenderedEmail OrderCancelled(Order order, StoreSettings store, BrandingOptions brand)
    {
        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(order.CustomerName))},</p>
            <p style="margin:0 0 12px">
              Order <strong>{Escape(order.OrderNumber)}</strong> has been cancelled. If anything was
              charged it has been released, and any reserved stock is back on the shelf.
            </p>
            """;

        var html = Layout(store, brand, $"Order {order.OrderNumber} cancelled", body);
        var text = $"""
            Hi {Display(order.CustomerName)},

            Order {order.OrderNumber} has been cancelled.

            {store.Name}
            """;

        return new RenderedEmail($"Order {order.OrderNumber} cancelled", html, text);
    }

    /// <summary>Refund confirmation for a one-shot full refund recorded by staff.</summary>
    public static RenderedEmail RefundIssued(Order order, decimal amount, StoreSettings store, BrandingOptions brand)
    {
        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(order.CustomerName))},</p>
            <p style="margin:0 0 12px">
              We have refunded <strong>{Money(amount, store)}</strong> for order
              <strong>{Escape(order.OrderNumber)}</strong> to your original payment method. Depending on
              your bank it can take a few days to appear on your statement.
            </p>
            """;

        var html = Layout(store, brand, $"Refund for order {order.OrderNumber}", body);
        var text = $"""
            Hi {Display(order.CustomerName)},

            We have refunded {amount:N2} for order {order.OrderNumber} to your original payment
            method. Depending on your bank it can take a few days to appear on your statement.

            {store.Name}
            """;

        return new RenderedEmail($"Refund issued for order {order.OrderNumber}", html, text);
    }

    /// <summary>Tells the shopper the shop approved or rejected their return request.</summary>
    public static RenderedEmail ReturnDecided(
        ReturnRequest request, bool approved, StoreSettings store, BrandingOptions brand)
    {
        var heading = approved
            ? $"Return {request.Reference} approved"
            : $"Return {request.Reference} rejected";

        var reason = string.IsNullOrWhiteSpace(request.DecisionNote)
            ? string.Empty
            : $"""
              <p style="margin:0 0 12px">
                The shop said: {Escape(request.DecisionNote)}
              </p>
              """;

        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(request.Order?.CustomerName ?? request.Email))},</p>
            <p style="margin:0 0 12px">
              {(approved
                  ? $"Your return <strong>{Escape(request.Reference)}</strong> for order <strong>{Escape(request.Order?.OrderNumber ?? "")}</strong> has been approved. Once the goods are received and checked, the refund is issued."
                  : $"Your return <strong>{Escape(request.Reference)}</strong> for order <strong>{Escape(request.Order?.OrderNumber ?? "")}</strong> has been rejected.")}
            </p>
            {reason}
            """;

        var html = Layout(store, brand, heading, body);
        var text = $"""
            Hi {Display(request.Order?.CustomerName ?? request.Email)},

            Your return {request.Reference} for order {request.Order?.OrderNumber}
            has been {(approved ? "approved" : "rejected")}.
            {(string.IsNullOrWhiteSpace(request.DecisionNote) ? string.Empty : $"The shop said: {request.DecisionNote}")}

            {store.Name}
            """;

        return new RenderedEmail($"{heading} — order {request.Order?.OrderNumber}", html, text);
    }

    /// <summary>Refund confirmation once a completed return has been paid back.</summary>
    public static RenderedEmail ReturnRefunded(ReturnRequest request, StoreSettings store, BrandingOptions brand)
    {
        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(request.Order?.CustomerName ?? request.Email))},</p>
            <p style="margin:0 0 12px">
              Your return <strong>{Escape(request.Reference)}</strong> for order
              <strong>{Escape(request.Order?.OrderNumber ?? "")}</strong> is complete. We have refunded
              <strong>{Money(request.RefundAmount, store)}</strong> to your original payment method.
            </p>
            <p style="margin:0 0 12px">
              Depending on your bank it can take a few days to appear on your statement.
            </p>
            """;

        var html = Layout(store, brand, $"Refunded: return {request.Reference}", body);
        var text = $"""
            Hi {Display(request.Order?.CustomerName ?? request.Email)},

            Your return {request.Reference} for order {request.Order?.OrderNumber} is complete.
            We have refunded {request.RefundAmount:N2} to your original payment method.

            {store.Name}
            """;

        return new RenderedEmail($"Refund issued for return {request.Reference}", html, text);
    }

    /// <summary>
    /// One message in a shopper–shop conversation, mailed to whichever side did not write it.
    /// </summary>
    public static RenderedEmail ConversationMessage(
        Conversation conversation, ConversationMessage message, StoreSettings store, BrandingOptions brand)
    {
        var shopName = conversation.Shop?.Name ?? "the shop";

        // The heading names the sender, because that is the one fact the recipient decides on.
        var heading = message.Author == ConversationAuthor.Customer
            ? $"New message from a shopper ({conversation.Reference})"
            : $"Reply from {shopName} ({conversation.Reference})";

        var from = message.Author == ConversationAuthor.Customer
            ? "A shopper"
            : message.Author == ConversationAuthor.Vendor
                ? shopName
                : store.Name;

        var body = $"""
            <p style="margin:0 0 12px">Hi there,</p>
            <p style="margin:0 0 12px">
              There is a new message on conversation <strong>{Escape(conversation.Reference)}</strong>
              &mdash; &ldquo;{Escape(conversation.Subject)}&rdquo;.
            </p>
            <p style="margin:0 0 4px;font-weight:600">{Escape(from)} wrote</p>
            <p style="margin:0 0 12px">{MessageBody(message.Body)}</p>
            <p style="margin:16px 0 0;color:{brand.InkSubtle};font-size:13px">
              Sign in to answer on the site &mdash; the conversation is kept there.
            </p>
            """;

        var html = Layout(store, brand, heading, body);
        var text = $"""
            Hi there,

            New message on conversation {conversation.Reference} — {conversation.Subject}.

            {from} wrote:

            {message.Body}

            {store.Name}
            """;

        return new RenderedEmail(heading, html, text);
    }

    /// <summary>Tells a shop's owner a payout was recorded against their balance.</summary>
    public static RenderedEmail PayoutIssued(VendorPayout payout, StoreSettings store, BrandingOptions brand)
    {
        var body = $"""
            <p style="margin:0 0 12px">Hi there,</p>
            <p style="margin:0 0 12px">
              A payout of <strong>{Money(payout.NetAmount, store)}</strong> for
              {Escape(payout.Shop?.Name ?? "your shop")} was recorded ({Escape(payout.Reference)})
              via {Escape(payout.Method)}.
            </p>
            <p style="margin:0 0 12px">
              It settles {payout.EntryCount} ledger
              {(payout.EntryCount == 1 ? "entry" : "entries")} covering
              {payout.PeriodStartUtc:yyyy-MM-dd} to {payout.PeriodEndUtc:yyyy-MM-dd}.
            </p>
            """;

        var html = Layout(store, brand, $"Payout {payout.Reference} recorded", body);
        var text = $"""
            Hi there,

            A payout of {payout.NetAmount:N2} for {(payout.Shop?.Name ?? "your shop")} was recorded
            ({payout.Reference}) via {payout.Method}. It settles {payout.EntryCount} ledger entries
            covering {payout.PeriodStartUtc:yyyy-MM-dd} to {payout.PeriodEndUtc:yyyy-MM-dd}.

            {store.Name}
            """;

        return new RenderedEmail($"Payout {payout.Reference} recorded", html, text);
    }

    /// <summary>
    /// Tells a shop's owner the marketplace approved, rejected, suspended or reinstated them —
    /// <paramref name="change"/> picks the wording, <paramref name="reason"/> is the administrator's
    /// note where one was given.
    /// </summary>
    public static RenderedEmail ShopStatusDecision(
        string shopName, string change, string? reason, StoreSettings store, BrandingOptions brand)
    {
        var heading = change switch
        {
            "approved" => $"{shopName} is approved",
            "rejected" => $"Application for {shopName} rejected",
            "suspended" => $"{shopName} suspended",
            "reinstated" => $"{shopName} is trading again",
            _ => $"{shopName}: shop status update"
        };

        var sentenceHtml = change switch
        {
            "approved" => $"Your shop <strong>{Escape(shopName)}</strong> has been approved and can now list products and take orders.",
            "rejected" => $"Your application to open <strong>{Escape(shopName)}</strong> has been rejected.",
            "suspended" => $"Your shop <strong>{Escape(shopName)}</strong> has been suspended and is no longer trading.",
            "reinstated" => $"Your shop <strong>{Escape(shopName)}</strong> has been reinstated and is trading again.",
            _ => $"Your shop <strong>{Escape(shopName)}</strong> status was updated ({Escape(change)})."
        };

        var sentenceText = change switch
        {
            "approved" => $"Your shop {shopName} has been approved and can now list products and take orders.",
            "rejected" => $"Your application to open {shopName} has been rejected.",
            "suspended" => $"Your shop {shopName} has been suspended and is no longer trading.",
            "reinstated" => $"Your shop {shopName} has been reinstated and is trading again.",
            _ => $"Your shop {shopName} status was updated ({change})."
        };

        var reasonHtml = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $"""
              <p style="margin:0 0 12px">Reason: {Escape(reason)}</p>
              """;

        var body = $"""
            <p style="margin:0 0 12px">Hi there,</p>
            <p style="margin:0 0 12px">{sentenceHtml}</p>
            {reasonHtml}
            """;

        var html = Layout(store, brand, heading, body);
        var text = $"""
            Hi there,

            {sentenceText}
            {(string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Reason: {reason}")}

            {store.Name}
            """;

        return new RenderedEmail(heading, html, text);
    }

    public static RenderedEmail ListingDecision(
        string productName, string decision, string? reason, StoreSettings store, BrandingOptions brand)
    {
        var heading = decision switch
        {
            "approved" => $"{productName} is approved",
            "rejected" => $"{productName} was not approved",
            "withdrawn" => $"{productName} was withdrawn",
            _ => $"{productName}: listing update"
        };

        var sentenceHtml = decision switch
        {
            "approved" => $"Your listing <strong>{Escape(productName)}</strong> has been approved and is now live in the catalogue.",
            "rejected" => $"Your listing <strong>{Escape(productName)}</strong> was not approved, so it is not live.",
            "withdrawn" => $"Your listing <strong>{Escape(productName)}</strong> has been withdrawn from the catalogue and is no longer live.",
            _ => $"Your listing <strong>{Escape(productName)}</strong> was updated ({Escape(decision)})."
        };

        var sentenceText = decision switch
        {
            "approved" => $"Your listing {productName} has been approved and is now live in the catalogue.",
            "rejected" => $"Your listing {productName} was not approved, so it is not live.",
            "withdrawn" => $"Your listing {productName} has been withdrawn from the catalogue and is no longer live.",
            _ => $"Your listing {productName} was updated ({decision})."
        };

        var reasonHtml = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $"""
              <p style="margin:0 0 12px">Reason: {Escape(reason)}</p>
              """;

        var body = $"""
            <p style="margin:0 0 12px">Hi there,</p>
            <p style="margin:0 0 12px">{sentenceHtml}</p>
            {reasonHtml}
            """;

        var html = Layout(store, brand, heading, body);
        var text = $"""
            Hi there,

            {sentenceText}
            {(string.IsNullOrWhiteSpace(reason) ? string.Empty : $"Reason: {reason}")}

            {store.Name}
            """;

        return new RenderedEmail(heading, html, text);
    }

    public static RenderedEmail PasswordReset(
        string displayName, string resetUrl, int validMinutes, StoreSettings store, BrandingOptions brand)
    {
        var body = $"""
            <p style="margin:0 0 12px">Hi {Escape(Display(displayName))},</p>
            <p style="margin:0 0 12px">
              Somebody asked to reset the password on your {Escape(store.Name)} account. If that was
              you, choose a new password using the link below. It expires in {validMinutes} minutes.
            </p>
            <p style="margin:20px 0">
              <a href="{Escape(resetUrl)}"
                 style="display:inline-block;background:{brand.Primary};color:#ffffff;text-decoration:none;
                        padding:12px 20px;border-radius:{brand.Radius}px;font-weight:600">
                Choose a new password
              </a>
            </p>
            <p style="margin:0 0 12px;color:{brand.InkSubtle};font-size:13px">
              If you did not ask for this, you can ignore this email — your password will not change.
            </p>
            """;

        var html = Layout(store, brand, "Reset your password", body);
        var text = $"""
            Hi {Display(displayName)},

            Somebody asked to reset the password on your {store.Name} account.
            Open this link to choose a new password (it expires in {validMinutes} minutes):

            {resetUrl}

            If you did not ask for this, ignore this email — your password will not change.

            {store.Name}
            """;

        return new RenderedEmail($"Reset your {store.Name} password", html, text);
    }

    public static RenderedEmail Test(StoreSettings store, BrandingOptions brand)
    {
        var body = """
            <p style="margin:0 0 12px">
              This is a test message from the store's transactional email outbox. If you can read it,
              the configured transport is delivering mail.
            </p>
            """;

        var html = Layout(store, brand, "Test email", body);
        var text = "This is a test message from the store's transactional email outbox.";

        return new RenderedEmail($"{store.Name} test email", html, text);
    }

    // ------------------------------------------------------------------
    // Shared chrome
    // ------------------------------------------------------------------

    private static string Layout(StoreSettings store, BrandingOptions brand, string heading, string body)
    {
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <body style="margin:0;padding:24px;background:{brand.SurfaceMuted};
                         font-family:{brand.FontStack};color:{brand.Ink}">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
                <tr>
                  <td align="center">
                    <table role="presentation" width="600" cellpadding="0" cellspacing="0"
                           style="width:600px;max-width:100%;background:{brand.Surface};border:1px solid {brand.Border};
                                  border-radius:{brand.Radius}px">
                      <tr>
                        <td style="padding:20px 28px;border-bottom:1px solid {brand.Border}">
                          <span style="font-size:18px;font-weight:700;color:{brand.Primary}">{Escape(store.Name)}</span>
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:28px">
                          <h1 style="margin:0 0 16px;font-size:20px;color:{brand.Ink}">{Escape(heading)}</h1>
                          {body}
                        </td>
                      </tr>
                      <tr>
                        <td style="padding:18px 28px;border-top:1px solid {brand.Border};
                                   color:{brand.InkSubtle};font-size:12px">
                          {Escape(store.Name)} · Need a hand? <a href="mailto:{Escape(store.SupportEmail)}"
                            style="color:{brand.Primary}">{Escape(store.SupportEmail)}</a>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string Lines(Order order, StoreSettings store, BrandingOptions brand)
    {
        var rows = string.Concat(order.Lines.Select(line => $"""
            <tr>
              <td style="padding:8px 0;border-bottom:1px solid {brand.Border}">
                {Escape(line.ProductName)}
                {(string.IsNullOrWhiteSpace(line.VariantName) ? string.Empty : $"<span style=\"color:{brand.InkSubtle}\"> · {Escape(line.VariantName)}</span>")}
              </td>
              <td style="padding:8px 0;border-bottom:1px solid {brand.Border};text-align:center;color:{brand.InkSubtle}">
                × {line.Quantity}
              </td>
              <td style="padding:8px 0;border-bottom:1px solid {brand.Border};text-align:right;white-space:nowrap">
                {Money(line.LineTotal, store)}
              </td>
            </tr>
            """));

        return $"""
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:8px 0 0;font-size:14px">
              {rows}
              <tr>
                <td colspan="2" style="padding:10px 0 0;color:{brand.InkMuted}">Subtotal</td>
                <td style="padding:10px 0 0;text-align:right">{Money(order.Subtotal, store)}</td>
              </tr>
              <tr>
                <td colspan="2" style="padding:2px 0;color:{brand.InkMuted}">Delivery</td>
                <td style="padding:2px 0;text-align:right">{(order.ShippingTotal == 0m ? "Free" : Money(order.ShippingTotal, store))}</td>
              </tr>
              {(order.TaxTotal == 0m ? string.Empty : $"""
              <tr>
                <td colspan="2" style="padding:2px 0;color:{brand.InkMuted}">Tax</td>
                <td style="padding:2px 0;text-align:right">{Money(order.TaxTotal, store)}</td>
              </tr>
              """)}
              {(order.DiscountTotal == 0m ? string.Empty : $"""
              <tr>
                <td colspan="2" style="padding:2px 0;color:{brand.Success}">Discount</td>
                <td style="padding:2px 0;text-align:right;color:{brand.Success}">−{Money(order.DiscountTotal, store)}</td>
              </tr>
              """)}
              <tr>
                <td colspan="2" style="padding:10px 0 0;font-weight:700;border-top:1px solid {brand.BorderStrong}">Total</td>
                <td style="padding:10px 0 0;text-align:right;font-weight:700;border-top:1px solid {brand.BorderStrong}">
                  {Money(order.GrandTotal, store)}
                </td>
              </tr>
            </table>
            """;
    }

    private static string TextLines(Order order, StoreSettings store) =>
        string.Join(Environment.NewLine, order.Lines.Select(line =>
            $"  {line.Quantity} × {line.ProductName}{(string.IsNullOrWhiteSpace(line.VariantName) ? string.Empty : $" ({line.VariantName})")} — {Money(line.LineTotal, store)}"));

    private static string Money(decimal amount, StoreSettings store) =>
        store.CurrencySymbol + amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Display(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "there" : name.Trim();

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    /// <summary>A message body as HTML: encoded, with the writer's line breaks preserved.</summary>
    private static string MessageBody(string value) =>
        Escape(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "<br />", StringComparison.Ordinal);
}
