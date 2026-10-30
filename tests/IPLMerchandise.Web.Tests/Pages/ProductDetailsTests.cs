using IPLMerchandise.Web.Client.Pages;
using IPLMerchandise.Web.Tests.Fakes;

namespace IPLMerchandise.Web.Tests.Pages;

public class ProductDetailsTests : StoreTestContext
{
    [Fact]
    public void Out_of_stock_product_cannot_be_added()
    {
        Catalog.Products.Add(Samples.Product("gone", "Sold Out Cap", 299m, 0, Contracts.V1.ProductType.Cap));
        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "gone"));

        cut.WaitForAssertion(() => Assert.Contains("currently out of stock", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid=add-to-cart]"));
    }

    [Fact]
    public async Task No_add_form_when_the_cart_already_holds_the_maximum()
    {
        SignInAs("Customer");
        CartApi.Cart = Samples.Cart(Samples.Line(Samples.KkrPhoto, 2));
        await CartState.LoadAsync();

        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "kkr-photo"));

        cut.WaitForAssertion(() => Assert.Contains("the limit for this item is 2 per order", cut.Find("[data-testid=limit-reached]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=add-to-cart]"));
    }

    [Fact]
    public async Task With_more_in_stock_than_one_order_may_hold_the_page_explains_the_limit_and_what_is_left()
    {
        SignInAs("Customer");
        Catalog.Products.Add(Samples.Product("gt-photo", "GT Signed Photo", 5499m, 3, Contracts.V1.ProductType.SignedPhotograph, "GT")); // 3 left, limit 2
        CartApi.Cart = Samples.Cart(Samples.Line(Catalog.Products[^1], 1));
        await CartState.LoadAsync();

        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "gt-photo"));

        cut.WaitForAssertion(() => Assert.Equal(
            "Limited edition: limit 2 per order. You have 1 in your cart, so you can add 1 more.",
            cut.Find("[data-testid=quantity-help]").TextContent));
        Assert.Contains("Only 3 left", cut.Markup);
    }

    [Theory]
    [InlineData("kkr-photo", "Limited edition: limit 2 per order.")]
    [InlineData("csk-cap", "Up to 10 per order.")]
    public void Against_an_api_that_does_not_send_the_optional_limit_the_documented_defaults_apply(string id, string expected)
    {
        // A new client talking to an older v1 server (before maxQuantityPerLine existed): DESIGN ADR-8, point 4.
        for (var i = 0; i < Catalog.Products.Count; i++)
            Catalog.Products[i] = Catalog.Products[i] with { MaxQuantityPerLine = null };

        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, id));

        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find("[data-testid=quantity-help]").TextContent));
    }

    [Fact]
    public void Quantity_stepper_stops_at_the_limit()
    {
        SignInAs("Customer");
        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "kkr-photo")); // limited edition: max 2
        cut.WaitForState(() => cut.FindAll("#quantity").Count == 1);

        cut.Find("button[aria-label='Increase quantity']").Click();
        cut.Find("button[aria-label='Increase quantity']").Click(); // already at 2: no further

        Assert.Equal("2", cut.Find("#quantity").GetAttribute("value"));
        Assert.True(cut.Find("button[aria-label='Increase quantity']").HasAttribute("disabled"));
        Assert.Equal("quantity", cut.FindAll("label").Single(l => l.TextContent.Trim() == "Quantity").GetAttribute("for"));
    }

    [Fact]
    public void Double_click_on_add_sends_one_request()
    {
        SignInAs("Customer");
        CartApi.Gate.Close(); // hold the first request in flight
        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "kkr-photo"));
        cut.WaitForState(() => cut.FindAll("[data-testid=add-to-cart]").Count == 1);

        cut.Find("form").Submit();
        cut.Find("form").Submit();
        CartApi.Gate.Open();

        cut.WaitForAssertion(() => Assert.Contains("Added to your cart", cut.Markup));
        Assert.Single(CartApi.Calls);
    }

    [Fact]
    public void Rejected_add_is_shown_as_an_announced_alert()
    {
        SignInAs("Customer");
        CartApi.FailNext.Enqueue(Samples.Problem(409, "insufficient-stock", "Only 1 of 'KKR Signed Photo' available; 2 requested."));
        var cut = RenderComponent<ProductDetails>(p => p.Add(x => x.Id, "kkr-photo"));
        cut.WaitForState(() => cut.FindAll("form").Count == 1);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[data-testid=problem]");
            Assert.Equal("alert", alert.GetAttribute("role"));
            Assert.Contains("Only 1 of 'KKR Signed Photo' available", alert.TextContent);
        });
    }
}
