using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Pages;
using IPLMerchandise.Web.Client.Pages.Admin;
using IPLMerchandise.Web.Tests.Fakes;

namespace IPLMerchandise.Web.Tests.Pages;

public class LoginTests : StoreTestContext
{
    [Fact]
    public void Empty_submit_shows_field_errors_and_does_not_call_the_api()
    {
        var cut = RenderComponent<Login>();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("The Email field is required.", cut.Markup));
        Assert.Equal(0, AuthApi.Calls);
    }

    [Fact]
    public void Every_input_has_a_label()
    {
        var cut = RenderComponent<Register>();
        foreach (var input in cut.FindAll("input"))
            Assert.NotEmpty(cut.FindAll($"label[for='{input.GetAttribute("id")}']"));
    }
}

public class NavigationTests : StoreTestContext
{
    [Fact]
    public void Home_and_shop_links_are_real_links()
    {
        var cut = RenderComponent<Client.Layout.NavMenu>();
        Assert.Equal("/", cut.FindAll("a").Single(a => a.TextContent.Trim() == "Home").GetAttribute("href"));
        Assert.Equal("shop", cut.FindAll("a").Single(a => a.TextContent.Trim() == "Shop").GetAttribute("href"));
    }
}

public class AdminProductEditTests : StoreTestContext
{
    public AdminProductEditTests() => SignInAs("Admin");

    private IRenderedComponent<AdminProductEdit> Render()
    {
        var cut = RenderComponent<AdminProductEdit>(p => p.Add(x => x.Id, "csk-cap"));
        cut.WaitForState(() => cut.FindAll("#price-reason").Count == 1);
        return cut;
    }

    [Fact]
    public void Cancel_leaves_without_saving()
    {
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = Render();
        cut.Find("#new-price").Change("549");
        cut.Find("[data-testid=cancel]").Click();

        Assert.EndsWith("/admin/products", nav.Uri);
        Assert.Empty(AdminApi.ETagsSent); // nothing was sent to the API
    }

    [Fact]
    public void Nothing_is_sent_when_nothing_changed()
    {
        var cut = Render();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("No changes to save.", cut.Markup));
        Assert.Empty(AdminApi.ETagsSent);
    }

    [Fact]
    public void A_price_change_needs_a_reason()
    {
        var cut = Render();
        cut.Find("#new-price").Change("549");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Give a reason for the price change", cut.Markup));
        Assert.Empty(AdminApi.ETagsSent);
    }

    [Fact]
    public void One_save_sends_only_the_changed_parts_chaining_the_etag()
    {
        var cut = Render();
        cut.Find("#new-price").Change("549");
        cut.Find("#price-reason").Change("Season pricing");
        cut.Find("#new-stock").Change("90");
        cut.Find("#stock-reason").Change("Recount");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Saved price and stock.", cut.Markup));
        // Details were unchanged so not sent; stock used the ETag returned by the price update.
        Assert.Equal(["\"etag-1\"", "\"etag-2\""], AdminApi.ETagsSent);
    }

    [Fact]
    public void Stale_data_conflict_is_shown_instead_of_pretending_to_save()
    {
        AdminApi.Fail = Samples.Problem(412, "concurrency-conflict", "The product was changed by someone else.");
        var cut = Render();
        cut.Find("#new-price").Change("549");
        cut.Find("#price-reason").Change("Season pricing");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Someone else changed this product", cut.Find("[data-testid=conflict]").TextContent));
        Assert.Contains("price was not saved", cut.Find("[data-testid=conflict]").TextContent);
        Assert.DoesNotContain("Saved price", cut.Markup);
        Assert.Equal("\"etag-1\"", AdminApi.ETagsSent.Single()); // the version the admin was looking at
    }
}

public class OrderHistoryTests : StoreTestContext
{
    public OrderHistoryTests() => SignInAs("Customer");

    [Fact]
    public void Utc_times_are_shown_in_ist_by_default()
    {
        OrdersApi.History.Add(new Contracts.V1.OrderSummaryDto(Guid.NewGuid(), new DateTime(2026, 4, 1, 18, 30, 0, DateTimeKind.Utc),
            Contracts.V1.OrderStatus.Confirmed, 6, 10494m));
        var cut = RenderComponent<OrderHistory>();

        cut.WaitForAssertion(() => Assert.Contains("02 Apr 2026, 12:00 AM IST", cut.Find("[data-testid=order-row]").TextContent));
        Assert.Contains("₹10,494.00", cut.Markup);
    }

    [Fact]
    public void Stats_and_order_rows_show_number_items_and_status()
    {
        var id = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        OrdersApi.History.Add(new Contracts.V1.OrderSummaryDto(id, DateTime.UtcNow, Contracts.V1.OrderStatus.Confirmed, 4, 2847m)
        {
            Items =
            [
                new("MI Jersey", "MI", Contracts.V1.ProductType.Jersey, 1), new("CSK Cap", "CSK", Contracts.V1.ProductType.Cap, 1),
                new("RCB Flag", "RCB", Contracts.V1.ProductType.Flag, 1), new("KKR Signed Photo", "KKR", Contracts.V1.ProductType.SignedPhotograph, 1)
            ]
        });
        var cut = RenderComponent<OrderHistory>();

        cut.WaitForAssertion(() => Assert.Contains("#IPL-1A2B3C4D", cut.Find("[data-testid=order-row]").TextContent));
        Assert.Contains("+1 more", cut.Find("[data-testid=order-row]").TextContent);
        Assert.Contains("Confirmed", cut.Find("[data-testid=order-row]").TextContent);
        Assert.Contains("₹2,847.00", cut.Find("[data-testid=order-stats]").TextContent);
    }

    [Fact]
    public void Order_details_show_confirmation_items_and_totals()
    {
        var id = Guid.NewGuid();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"orders/{id}?placed=true");
        var cut = RenderComponent<OrderDetails>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() => Assert.Contains("Your order is confirmed", cut.Find("[data-testid=order-confirmed]").TextContent));
        Assert.Contains("CSK Cap", cut.Find("[data-testid=order-line]").TextContent);
        Assert.Contains("₹1,497.00", cut.Find("[data-testid=order-total]").TextContent);
        Assert.Contains(Client.Formatting.OrderNumber.Format(id), cut.Find("h1").TextContent);
        Assert.Contains("No delivery address was recorded", cut.Markup); // older orders placed without an address
    }

    [Fact]
    public void Date_filter_sends_whole_ist_days_as_utc_instants()
    {
        var cut = RenderComponent<OrderHistory>();
        cut.WaitForState(() => OrdersApi.ListCalls.Count == 1);
        var range = cut.FindComponents<MudBlazor.MudSelect<string>>().Single(c => c.Instance.Label == "Date Range");
        cut.InvokeAsync(() => range.Instance.ValueChanged.InvokeAsync("custom"));
        cut.WaitForState(() => cut.FindAll("#from-date").Count == 1);

        cut.Find("#from-date").Change("2026-04-02");
        cut.Find("#to-date").Change("2026-04-02");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Equal(2, OrdersApi.ListCalls.Count));
        var (from, to) = OrdersApi.ListCalls[1];
        Assert.Equal(new DateTime(2026, 4, 1, 18, 30, 0, DateTimeKind.Utc), from!.Value.UtcDateTime);
        Assert.Equal(new DateTime(2026, 4, 2, 18, 30, 0, DateTimeKind.Utc), to!.Value.UtcDateTime);
    }
}

public class AdminAuditTests : StoreTestContext
{
    public AdminAuditTests() => SignInAs("Admin");

    [Fact]
    public void Order_changes_show_the_customer_facing_order_number_and_tolerate_a_missing_id()
    {
        var orderId = Guid.Parse("1a2b3c4d-0000-0000-0000-000000000000");
        AuditEntryDto Entry(string? order) => new("audit:1", "csk-cap", AuditField.Stock, 100, 97, "customer:x", DateTime.UtcNow,
            AuditReason.Order, $"Order {order}", order);
        AdminApi.AuditEntries.AddRange([Entry(orderId.ToString("D")), Entry(null), Entry("short")]);

        var cut = RenderComponent<AdminAudit>(p => p.Add(x => x.Id, "csk-cap"));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=audit-row]").Count));
        var reasons = cut.FindAll("[data-testid=audit-row] td:nth-child(5)").Select(td => td.TextContent).ToList();
        Assert.Equal(["Order #IPL-1A2B3C4D", "Order", "Order"], reasons);
    }
}
