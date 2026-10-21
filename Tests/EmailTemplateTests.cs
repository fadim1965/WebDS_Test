using WebDS_Test.Domain;
using WebDS_Test.Services.Email;
using Xunit;

namespace WebDS_Test.Tests;

/// <summary>
/// Templates are pure functions, which makes them the cheapest place to pin down behaviour: no
/// database, no request, no transport. The escaping tests matter most — a customer's name is
/// attacker-controlled text and it ends up inside an HTML document.
/// </summary>
public class EmailTemplateTests
{
    private static readonly StoreSettings Store = TestSupport.Store();
    private static readonly BrandingOptions Brand = TestSupport.Brand();

    /// <summary>Every template, with a label so a failure names the one that broke.</summary>
    private static IEnumerable<(string Name, RenderedEmail Rendered)> AllTemplates()
    {
        var shopper = TestSupport.User();
        var order = TestSupport.Order();

        yield return ("welcome", EmailTemplates.Welcome(shopper, Store, Brand));
        yield return ("order-confirmation", EmailTemplates.OrderConfirmation(order, Store, Brand));
        yield return ("order-shipped", EmailTemplates.OrderShipped(order, Store, Brand));
        yield return ("order-cancelled", EmailTemplates.OrderCancelled(order, Store, Brand));
        yield return ("password-reset",
            EmailTemplates.PasswordReset("Sam Shopper", "https://shop.example/reset?token=abc", 120, Store, Brand));
        yield return ("test", EmailTemplates.Test(Store, Brand));
    }

    [Fact]
    public void Every_template_fills_in_a_subject_a_html_body_and_a_text_body()
    {
        foreach (var (name, rendered) in AllTemplates())
        {
            Assert.False(string.IsNullOrWhiteSpace(rendered.Subject), $"{name}: empty subject");
            Assert.False(string.IsNullOrWhiteSpace(rendered.Html), $"{name}: empty html");
            Assert.False(string.IsNullOrWhiteSpace(rendered.Text), $"{name}: empty text");
        }
    }

    [Fact]
    public void Every_html_body_is_a_complete_document()
    {
        foreach (var (_, rendered) in AllTemplates())
        {
            Assert.Contains("<!DOCTYPE html>", rendered.Html);
            Assert.Contains("</html>", rendered.Html);
        }
    }

    [Fact]
    public void Html_escapes_a_hostile_customer_name_instead_of_embedding_it()
    {
        var order = TestSupport.Order(customerName: "<script>alert('xss')</script>");

        var rendered = EmailTemplates.OrderConfirmation(order, Store, Brand);

        Assert.DoesNotContain("<script>", rendered.Html);
        Assert.Contains("&lt;script&gt;", rendered.Html);
    }

    [Fact]
    public void Html_escapes_an_ampersand_in_a_name()
    {
        var rendered = EmailTemplates.Welcome(
            TestSupport.User(displayName: "Tom & Jerry"), Store, Brand);

        Assert.Contains("Tom &amp; Jerry", rendered.Html);
        Assert.DoesNotContain("Tom & Jerry", rendered.Html);
    }

    [Fact]
    public void Order_confirmation_carries_the_reference_the_items_and_the_total()
    {
        var order = TestSupport.Order(number: "ORD-2026-0042");

        var rendered = EmailTemplates.OrderConfirmation(order, Store, Brand);

        Assert.Contains("ORD-2026-0042", rendered.Subject);
        Assert.Contains("ORD-2026-0042", rendered.Html);
        Assert.Contains("EMS Priority Envelope", rendered.Html);
        Assert.Contains("58.89", rendered.Html);          // grand total, symbol aside
        Assert.Contains("ORD-2026-0042", rendered.Text);
        Assert.Contains("EMS Priority Envelope", rendered.Text);
    }

    [Fact]
    public void Order_confirmation_shows_where_it_is_going()
    {
        var rendered = EmailTemplates.OrderConfirmation(TestSupport.Order(), Store, Brand);

        Assert.Contains("12 Rue Verdun", rendered.Html);
        Assert.Contains("Beirut", rendered.Html);
        Assert.Contains("12 Rue Verdun", rendered.Text);
    }

    [Fact]
    public void Shipped_notice_carries_the_tracking_number_when_there_is_one()
    {
        var order = TestSupport.Order(status: OrderStatus.Shipped);
        order.TrackingNumber = "LP123456789LB";
        order.Carrier = "libanpost";

        var rendered = EmailTemplates.OrderShipped(order, Store, Brand);

        Assert.Contains("LP123456789LB", rendered.Html);
        Assert.Contains("LP123456789LB", rendered.Text);
    }

    [Fact]
    public void Shipped_notice_reads_sensibly_without_a_tracking_number()
    {
        var order = TestSupport.Order(status: OrderStatus.Shipped);
        order.TrackingNumber = null;

        var rendered = EmailTemplates.OrderShipped(order, Store, Brand);

        Assert.Contains("on its way", rendered.Html);
        Assert.DoesNotContain("<strong></strong>", rendered.Html);
    }

    [Fact]
    public void Password_reset_contains_the_link_and_the_lifetime()
    {
        const string url = "https://shop.example/Account/ResetPassword?token=CfDJ8EXAMPLE";

        var rendered = EmailTemplates.PasswordReset("Sam Shopper", url, 120, Store, Brand);

        Assert.Contains(url, rendered.Html);
        Assert.Contains(url, rendered.Text);
        Assert.Contains("120 minutes", rendered.Text);
    }

    [Fact]
    public void A_reset_url_with_an_ampersand_is_escaped_in_the_html_href_only()
    {
        const string url = "https://shop.example/reset?token=a&return=b";

        var rendered = EmailTemplates.PasswordReset("Sam", url, 60, Store, Brand);

        Assert.Contains("&amp;return=b", rendered.Html);   // safe inside the attribute
        Assert.Contains(url, rendered.Text);                // untouched in plain text
    }
}
