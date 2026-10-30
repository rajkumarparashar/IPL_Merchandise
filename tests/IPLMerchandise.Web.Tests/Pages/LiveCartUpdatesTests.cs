using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Pages;
using IPLMerchandise.Web.Tests.Fakes;

namespace IPLMerchandise.Web.Tests.Pages;

/// <summary>Price and stock changes reach an open cart or checkout by push, with no Refresh click and no polling.</summary>
public class LiveCartUpdatesTests : StoreTestContext
{
    private static readonly CartDto Clean = Samples.Cart(Samples.Line(Samples.CskCap, 3));
    private static readonly CartDto PriceRaised = Samples.Cart(Samples.Line(Samples.CskCap, 3, currentPrice: 549.00m, CartLineIssue.PriceChanged));

    public LiveCartUpdatesTests()
    {
        SignInAs("Customer");
        CartApi.Cart = Clean;
    }

    [Fact]
    public void The_cart_page_follows_exactly_the_products_in_the_cart()
    {
        var cut = RenderComponent<CartPage>();
        cut.WaitForAssertion(() => Assert.Equal(["csk-cap"], ProductUpdates.Watched.Last()));

        DisposeComponents();
        Assert.Empty(ProductUpdates.Watched.Last()); // leaving the page stops following them
    }

    [Fact]
    public void A_pushed_price_change_flags_the_cart_and_blocks_checkout_without_a_refresh_click()
    {
        var cut = RenderComponent<CartPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(ProductUpdates.Watched));
        Assert.Empty(cut.FindAll("[data-testid=review-banner]"));

        CartApi.Cart = PriceRaised; // an admin raised the price
        ProductUpdates.Push("csk-cap");

        cut.WaitForAssertion(() => Assert.Contains("Price changed from ₹499.00 to ₹549.00", cut.Markup));
        Assert.True(cut.Find("[data-testid=go-to-checkout]").HasAttribute("disabled"));
    }

    [Fact]
    public void Changes_to_products_not_in_the_cart_cause_no_request()
    {
        var cut = RenderComponent<CartPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(ProductUpdates.Watched));
        var reads = CartApi.Gets;

        ProductUpdates.Push("mi-jersey");

        Assert.Equal(reads, CartApi.Gets);
    }

    [Fact]
    public void After_a_reconnect_the_cart_is_rechecked_because_pushes_may_have_been_missed()
    {
        var cut = RenderComponent<CartPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(ProductUpdates.Watched));

        CartApi.Cart = PriceRaised;
        ProductUpdates.Reconnect();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=review-banner]")));
    }

    [Fact]
    public void Checkout_shows_a_pushed_change_and_the_new_total_can_be_accepted_there()
    {
        CartApi.NextCart = Samples.Cart(Samples.Line(Samples.CskCap with { Price = 549.00m }, 3));
        var cut = RenderComponent<Checkout>();
        cut.WaitForAssertion(() => Assert.NotEmpty(ProductUpdates.Watched));

        CartApi.Cart = PriceRaised;
        ProductUpdates.Push("csk-cap");

        cut.WaitForAssertion(() => Assert.Contains("₹1,647.00", cut.Find("[data-testid=review-banner]").TextContent));
        cut.Find("[data-testid=accept-changes]").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=review-banner]")));
        Assert.Contains("₹1,647.00", cut.Find("[data-testid=checkout-total]").TextContent);
    }
}
