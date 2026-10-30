using IPLMerchandise.Web.Client.Api;
using IPLMerchandise.Web.Client.Pages;
using MudBlazor;

namespace IPLMerchandise.Web.Tests.Pages;

public class CatalogueTests : StoreTestContext
{
    [Fact]
    public void Lists_products_with_rupee_prices_and_stock_status()
    {
        var cut = RenderComponent<Catalogue>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=product-card]").Count));
        Assert.Contains("₹1,999.00", cut.Markup);
        Assert.Contains("Only 2 left", cut.Markup); // KKR photo, stock 2
    }

    [Fact]
    public void Pages_forward_and_back_with_continuation_tokens()
    {
        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=product-card]").Count == 4);
        var pageSize = cut.FindComponents<MudSelect<int>>().Single();
        cut.InvokeAsync(() => pageSize.Instance.ValueChanged.InvokeAsync(2));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("Showing 1–2 of 4 products", cut.Find("[data-testid=result-summary]").TextContent));
        cut.Find("[data-testid=next-page]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Showing 3–4 of 4 products", cut.Find("[data-testid=result-summary]").TextContent));
        cut.Find("[data-testid=prev-page]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Showing 1–2 of 4", cut.Find("[data-testid=result-summary]").TextContent));
    }

    [Fact]
    public void Shows_a_loading_state_while_waiting()
    {
        Catalog.Gate.Close();
        var cut = RenderComponent<Catalogue>();

        Assert.Contains("Loading products", cut.Find("[role=status]").TextContent);

        Catalog.Gate.Open();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=product-card]").Count));
    }

    [Fact]
    public void Failure_is_announced_and_retry_recovers()
    {
        Catalog.FailNext.Enqueue(ApiProblem.Network());
        var cut = RenderComponent<Catalogue>();

        cut.WaitForAssertion(() => Assert.Contains("Connection problem", cut.Find("[role=alert]").TextContent));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Retry").Click();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=product-card]").Count));
    }

    [Fact]
    public void Add_to_cart_from_the_card_adds_one_unit()
    {
        SignInAs("Customer");
        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=card-add-to-cart]").Count == 4);

        cut.Find("button[aria-label='Add CSK Cap to cart']").Click();

        cut.WaitForAssertion(() => Assert.Equal("Added CSK Cap to your cart.", cut.Find("[data-testid=cart-announcement]").TextContent));
        Assert.Equal("add:csk-cap:1", Assert.Single(CartApi.Calls).Operation);
    }

    [Fact]
    public void Add_to_cart_when_signed_out_goes_to_login()
    {
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=card-add-to-cart]").Count == 4);

        cut.Find("button[aria-label='Add CSK Cap to cart']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("login?returnUrl=products%2Fcsk-cap", nav.Uri));
        Assert.Empty(CartApi.Calls);
    }

    [Fact]
    public void Only_the_clicked_card_shows_progress_while_adding()
    {
        SignInAs("Customer");
        CartApi.Gate.Close(); // hold the request in flight
        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=card-add-to-cart]").Count == 4);

        cut.Find("button[aria-label='Add CSK Cap to cart']").Click();

        var clicked = cut.Find("button[aria-label='Add CSK Cap to cart']");
        Assert.True(clicked.HasAttribute("disabled"));
        Assert.Equal("true", clicked.GetAttribute("aria-busy"));
        Assert.Contains("Add to cart", clicked.TextContent); // label unchanged, so nothing next to it moves
        Assert.False(cut.Find("button[aria-label='Add MI Jersey to cart']").HasAttribute("disabled"));
        CartApi.Gate.Open();
    }

    [Fact]
    public async Task No_add_button_when_out_of_stock_or_the_cart_already_holds_the_maximum()
    {
        SignInAs("Customer");
        Catalog.Products.Add(Fakes.Samples.Product("gone", "Sold Out Cap", 299m, 0, Contracts.V1.ProductType.Cap));
        CartApi.Cart = Fakes.Samples.Cart(Fakes.Samples.Line(Fakes.Samples.KkrPhoto, 2)); // limited edition: 2 is the maximum
        await CartState.LoadAsync();

        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=product-card]").Count == 5);

        Assert.Empty(cut.FindAll("button[aria-label='Add Sold Out Cap to cart']"));
        Assert.Empty(cut.FindAll("button[aria-label='Add KKR Signed Photo to cart']"));
        Assert.Single(cut.FindAll("[data-testid=limit-reached]"));                     // the photo card explains why
        Assert.NotEmpty(cut.FindAll("button[aria-label='Add CSK Cap to cart']"));       // others unaffected
    }

    [Fact]
    public void Sidebar_checkboxes_show_counts_and_filter_the_list()
    {
        var cut = RenderComponent<Catalogue>();
        cut.WaitForState(() => cut.FindAll("[data-testid=product-card]").Count == 4);
        Assert.Contains("Cap (1)", cut.Find("aside").TextContent);

        var capBox = cut.FindAll("aside label").First(l => l.TextContent.Contains("Cap (1)")).QuerySelector("input")!;
        capBox.Change(true);

        cut.WaitForAssertion(() => Assert.Equal("CSK Cap", Assert.Single(cut.FindAll("[data-testid=product-card] h2 a")).TextContent.Trim()));
    }
}
