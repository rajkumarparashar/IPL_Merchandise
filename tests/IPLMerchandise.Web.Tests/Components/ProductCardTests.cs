using IPLMerchandise.Web.Client.Components;
using IPLMerchandise.Web.Tests.Fakes;

namespace IPLMerchandise.Web.Tests.Components;

public class ProductCardTests : StoreTestContext
{
    [Fact]
    public void Featured_products_show_a_ribbon_and_others_do_not()
    {
        var featured = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Samples.CskCap with { IsFeatured = true }));
        var plain = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Samples.CskCap with { IsFeatured = false }));

        Assert.Contains("Featured", featured.Find("[data-testid=featured-ribbon]").TextContent);
        Assert.Contains("(featured product)", featured.Find("h2").TextContent); // announced to screen readers too
        Assert.Empty(plain.FindAll("[data-testid=featured-ribbon]"));
    }

    [Fact]
    public void A_limited_edition_card_shows_its_per_order_limit_next_to_the_stock()
    {
        var photo = RenderComponent<ProductCard>(p => p
            .Add(x => x.Product, Samples.Product("gt-photo", "GT Signed Photo", 5499m, 3, Contracts.V1.ProductType.SignedPhotograph, "GT")));
        var cap = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Samples.CskCap));

        Assert.Equal("Only 3 left", photo.Find("[data-testid=stock-status]").TextContent);
        Assert.Equal("Limited edition · limit 2 per order", photo.Find("[data-testid=order-limit]").TextContent);
        Assert.Empty(cap.FindAll("[data-testid=order-limit]"));
    }

    [Fact]
    public void When_nothing_more_can_be_added_the_card_says_which_rule_applies()
    {
        var photo = Samples.Product("gt-photo", "GT Signed Photo", 5499m, 3, Contracts.V1.ProductType.SignedPhotograph, "GT");
        var lastCaps = Samples.Product("rr-cap", "RR Cap", 549m, 2, Contracts.V1.ProductType.Cap, "RR");
        IRenderedComponent<ProductCard> Card(Contracts.V1.ProductDto product, int inCart) => RenderComponent<ProductCard>(p => p
            .Add(x => x.Product, product).Add(x => x.CanAdd, false).Add(x => x.InCart, inCart)
            .Add(x => x.OnAddToCart, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<Contracts.V1.ProductDto>(this, _ => { })));

        Assert.Equal("Limit of 2 per order reached", Card(photo, 2).Find("[data-testid=limit-reached]").TextContent);
        Assert.Equal("All 2 available are in your cart", Card(lastCaps, 2).Find("[data-testid=limit-reached]").TextContent);
    }

    [Fact]
    public void Shows_the_photo_and_falls_back_to_the_illustration_if_it_cannot_load()
    {
        var withPhoto = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Samples.CskCap with { ImageUrl = "https://images.test/cap.png" }));
        var without = RenderComponent<ProductCard>(p => p.Add(x => x.Product, Samples.CskCap));

        Assert.Equal("https://images.test/cap.png", withPhoto.Find("[data-testid=product-photo]").GetAttribute("src"));
        Assert.Empty(without.FindAll("img"));
        Assert.Single(without.FindAll("svg.merch-art"));

        withPhoto.Find("img").TriggerEvent("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        Assert.Empty(withPhoto.FindAll("img"));
        Assert.Single(withPhoto.FindAll("svg.merch-art"));
    }
}
