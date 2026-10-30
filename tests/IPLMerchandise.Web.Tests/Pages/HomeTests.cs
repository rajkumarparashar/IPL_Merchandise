using IPLMerchandise.Web.Client.Pages;

namespace IPLMerchandise.Web.Tests.Pages;

public class HomeTests : StoreTestContext
{
    [Fact]
    public void Home_is_a_landing_page_that_links_into_the_shop()
    {
        Catalog.Products.Add(Fakes.Samples.Product("plain", "Not Featured Flag", 99m, 10, Contracts.V1.ProductType.Flag, "DC") with { IsFeatured = false });
        var cut = RenderComponent<Home>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=product-card]").Count));
        var categories = cut.FindAll("[data-testid=category-tile]").ToList();
        Assert.Equal("shop?type=Jersey", categories[0].GetAttribute("href"));
        Assert.DoesNotContain("Not Featured Flag", cut.Markup.Split("featured-title")[1]);   // only featured products in that section

        var teams = cut.FindAll("[data-testid=team-tile]").Select(t => t.GetAttribute("href")).ToList();
        Assert.Equal(5, teams.Count);                       // only franchises that have products
        Assert.NotNull(cut.Find("button[aria-label='Previous teams']"));
        Assert.NotNull(cut.Find("button[aria-label='Next teams']"));
        Assert.Contains("shop?franchise=CSK", teams);
        Assert.Contains("Your Team. Your Pride.", cut.Markup);
    }
}
