using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Api;
using IPLMerchandise.Web.Client.Pages;
using IPLMerchandise.Web.Tests.Fakes;

namespace IPLMerchandise.Web.Tests.Pages;

public class CartPageTests : StoreTestContext
{
    public CartPageTests() => SignInAs("Customer");

    [Fact]
    public void Price_change_warning_blocks_checkout_until_accepted()
    {
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.CskCap, 3, currentPrice: 549.00m, CartLineIssue.PriceChanged));
        CartApi.NextCart = Samples.Cart(Samples.Line(Samples.CskCap with { Price = 549.00m }, 3));
        var cut = RenderComponent<CartPage>();

        cut.WaitForAssertion(() => Assert.Contains("Price changed from ₹499.00 to ₹549.00", cut.Markup));
        Assert.Contains("₹1,647.00", cut.Find("[data-testid=review-banner]").TextContent);
        Assert.True(cut.Find("[data-testid=go-to-checkout]").HasAttribute("disabled"));

        cut.Find("[data-testid=accept-changes]").Click();

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=go-to-checkout]").HasAttribute("disabled")));
    }

    [Fact]
    public void A_line_at_the_per_order_limit_says_so_when_more_is_in_stock()
    {
        var photo = Samples.Product("gt-photo", "GT Signed Photo", 5499m, 3, ProductType.SignedPhotograph, "GT");
        CartApi.Cart = Samples.Cart(Samples.Line(photo, 2) with { MaxQuantity = 2 });
        var cut = RenderComponent<CartPage>();

        cut.WaitForAssertion(() => Assert.Equal("Limit 2 per order", cut.Find("[data-testid=line-limit]").TextContent));
        Assert.True(cut.Find("button[aria-label='Increase quantity of GT Signed Photo']").HasAttribute("disabled"));
    }

    [Fact]
    public void Retry_after_a_network_failure_reuses_the_same_request_id()
    {
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.CskCap, 3));
        CartApi.FailNext.Enqueue(ApiProblem.Network());
        var cut = RenderComponent<CartPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid=cart-line]").Count == 1);

        cut.Find("button[aria-label='Remove CSK Cap']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Connection problem", cut.Find("[role=alert]").TextContent));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, CartApi.Calls.Count));
        Assert.Equal(CartApi.Calls[0].Key, CartApi.Calls[1].Key);
    }

    [Fact]
    public void Quantity_stepper_sends_one_update_per_click()
    {
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.CskCap, 3));
        var cut = RenderComponent<CartPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid=cart-line]").Count == 1);

        cut.Find("button[aria-label='Increase quantity of CSK Cap']").Click();

        cut.WaitForAssertion(() => Assert.Equal("update:csk-cap:4", Assert.Single(CartApi.Calls).Operation));
    }

    [Fact]
    public void Clear_cart_asks_for_confirmation_then_empties_the_cart()
    {
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.CskCap, 3), Samples.Line(Samples.MiJersey, 1));
        CartApi.NextCart = Samples.Cart();
        var cut = RenderComponent<CartPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid=cart-line]").Count == 2);

        cut.Find("[data-testid=clear-cart]").Click();
        Assert.Empty(CartApi.Calls);                       // nothing happens until confirmed
        cut.Find("[data-testid=confirm-clear]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Your cart is empty", cut.Markup));
        Assert.Equal("clear", Assert.Single(CartApi.Calls).Operation);
    }

    [Fact]
    public void Every_button_has_an_accessible_name_and_the_table_has_a_caption()
    {
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.CskCap, 1));
        var cut = RenderComponent<CartPage>();
        cut.WaitForState(() => cut.FindAll("[data-testid=cart-line]").Count == 1);

        Assert.All(cut.FindAll("button"), b =>
            Assert.False(string.IsNullOrWhiteSpace(b.GetAttribute("aria-label") ?? b.TextContent), b.OuterHtml));
        Assert.NotNull(cut.Find("table caption"));
    }
}

public class CheckoutTests : StoreTestContext
{
    public CheckoutTests()
    {
        SignInAs("Customer");
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.MiJersey, 2), Samples.Line(Samples.CskCap, 3), Samples.Line(Samples.KkrPhoto, 1));
    }

    /// <summary>Fills the Shipping &amp; Payment step and moves to Review.</summary>
    private static void CompleteDetails(IRenderedComponent<Checkout> cut, string pin = "500049", bool acceptTerms = true)
    {
        cut.WaitForState(() => cut.FindAll("#mobile").Count == 1);
        cut.Find("#mobile").Change("9876543210");
        cut.Find("#line1").Change("H No 12-6-87, Green Park");
        cut.Find("#city").Change("Hyderabad");
        var state = cut.FindComponents<MudBlazor.MudSelect<string>>().Single();
        cut.InvokeAsync(() => state.Instance.ValueChanged.InvokeAsync("Telangana"));
        cut.Find("#pinCode").Change(pin);
        if (acceptTerms) cut.Find("input[type=checkbox]").Change(true);
        cut.Find("form").Submit();
    }

    private IRenderedComponent<Checkout> AtReview()
    {
        var cut = RenderComponent<Checkout>();
        CompleteDetails(cut);
        cut.WaitForState(() => cut.FindAll("[data-testid=place-order]").Count == 1);
        return cut;
    }

    [Fact]
    public void Invalid_address_keeps_the_customer_on_the_details_step()
    {
        var cut = RenderComponent<Checkout>();
        CompleteDetails(cut, pin: "05004");

        cut.WaitForAssertion(() => Assert.Contains("Enter a valid 6-digit PIN code.", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid=place-order]"));
    }

    [Fact]
    public void Terms_must_be_accepted()
    {
        var cut = RenderComponent<Checkout>();
        CompleteDetails(cut, acceptTerms: false);

        cut.WaitForAssertion(() => Assert.Contains("accept the terms", cut.Find("[role=alert]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=place-order]"));
    }

    [Fact]
    public void Terms_open_in_a_dialog_without_ticking_the_checkbox()
    {
        var dialogs = RenderComponent<MudBlazor.MudDialogProvider>();
        var cut = RenderComponent<Checkout>();
        cut.WaitForState(() => cut.FindAll("[data-testid=terms-link]").Count == 1);

        cut.Find("[data-testid=terms-link]").Click();

        dialogs.WaitForAssertion(() => Assert.Contains("Orders and stock", dialogs.Markup));
        Assert.False(((AngleSharp.Html.Dom.IHtmlInputElement)cut.Find("input[type=checkbox]")).IsChecked);
        dialogs.Find("[data-testid=close-terms]").Click();
        dialogs.WaitForAssertion(() => Assert.DoesNotContain("Orders and stock", dialogs.Markup));
    }

    [Fact]
    public void Address_is_reviewed_and_sent_with_the_order()
    {
        var cut = AtReview();
        Assert.Contains("Hyderabad, Telangana 500049", cut.Find("[data-testid=review-address]").TextContent);

        cut.Find("[data-testid=place-order]").Click();

        cut.WaitForAssertion(() => Assert.Equal("500049", Assert.Single(OrdersApi.Addresses)!.PinCode));
        Assert.Equal("Asha", OrdersApi.Addresses[0]!.FullName); // pre-filled from the signed-in user
    }

    [Fact]
    public void Double_click_places_one_order()
    {
        OrdersApi.Gate.Close();
        var cut = AtReview();
        Assert.Contains("Place order for ₹10,494.00", cut.Find("[data-testid=place-order]").TextContent);

        cut.Find("[data-testid=place-order]").Click();
        cut.Find("[data-testid=place-order]").Click();
        OrdersApi.Gate.Open();

        cut.WaitForAssertion(() => Assert.Single(OrdersApi.Checkouts));
    }

    [Fact]
    public void Retry_after_a_lost_response_reuses_the_same_order_id()
    {
        OrdersApi.FailNext.Enqueue(ApiProblem.Network("The store took too long to answer."));
        var cut = AtReview();

        cut.Find("[data-testid=place-order]").Click();
        cut.WaitForAssertion(() => Assert.Contains("took too long", cut.Find("[role=alert]").TextContent));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, OrdersApi.Checkouts.Count));
        Assert.Equal(OrdersApi.Checkouts[0].OrderId, OrdersApi.Checkouts[1].OrderId);
    }

    [Fact]
    public void Out_of_stock_rejection_lists_the_failing_lines()
    {
        OrdersApi.FailNext.Enqueue(Samples.Problem(409, "checkout-rejected", "Some items cannot be supplied.") with
        {
            FailingLines = [new CheckoutFailureLine("kkr-photo", "KKR Signed Photo", 1, 0, "out-of-stock")]
        });
        var cut = AtReview();

        cut.Find("[data-testid=place-order]").Click();

        cut.WaitForAssertion(() => Assert.Contains("KKR Signed Photo", cut.Find("[data-testid=failing-lines]").TextContent));
        Assert.Equal("alert", cut.Find("[data-testid=problem]").GetAttribute("role"));
    }
}
