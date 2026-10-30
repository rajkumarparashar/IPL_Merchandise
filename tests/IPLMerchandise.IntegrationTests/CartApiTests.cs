using System.Net;
using System.Net.Http.Json;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.IntegrationTests.Infrastructure;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CartApiTests(ApiFactory factory)
{
    [Theory]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(10, HttpStatusCode.OK)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(11, HttpStatusCode.BadRequest)]
    public async Task Quantity_boundaries(int quantity, HttpStatusCode expected)
    {
        var customer = await TestClient.CustomerAsync(factory);
        Assert.Equal(expected, (await customer.AddToCartAsync("rcb-flag", quantity)).StatusCode);
    }

    [Fact]
    public async Task Stock_of_exactly_one_allows_one_and_rejects_two_with_409()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"last-one-{Guid.NewGuid():N}", 50m, 1);
        var customer = await TestClient.CustomerAsync(factory);

        var tooMany = await customer.AddToCartAsync(product.Id, 2);
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Equal("insufficient-stock", await TestClient.CodeAsync(tooMany));

        Assert.Equal(1, (await customer.AddToCartOkAsync(product.Id, 1)).Lines.Single().Quantity);
    }

    [Fact]
    public async Task Repeated_request_id_is_applied_once_and_returns_the_original_result()
    {
        var customer = await TestClient.CustomerAsync(factory);
        var key = TestClient.NewKey();

        var first = await customer.AddToCartAsync("rcb-flag", 2, key);
        var retry = await customer.AddToCartAsync("rcb-flag", 2, key);

        Assert.Equal("true", retry.Headers.GetValues(ApiHeaders.IdempotentReplay).Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(2, (await customer.GetCartAsync()).Lines.Single().Quantity);

        // The same id for a different change is refused rather than silently replayed.
        Assert.Equal(HttpStatusCode.Conflict, (await customer.AddToCartAsync("rcb-flag", 5, key)).StatusCode);
    }

    [Fact]
    public async Task Simultaneous_duplicates_are_applied_once()
    {
        var customer = await TestClient.CustomerAsync(factory);
        var key = TestClient.NewKey();

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => customer.AddToCartAsync("rcb-flag", 1, key)));

        Assert.Equal(1, (await customer.GetCartAsync()).Lines.Single().Quantity);
    }

    [Fact]
    public async Task Change_quantity_and_remove()
    {
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync("rcb-flag", 1);

        var updated = await customer.SendCartAsync(HttpMethod.Put, "/api/v1/cart/items/rcb-flag", new UpdateCartItemRequest { Quantity = 4 });
        Assert.Equal(4, (await updated.Content.ReadFromJsonAsync<CartDto>(TestClient.Json))!.Lines.Single().Quantity);

        var removed = await customer.SendCartAsync(HttpMethod.Delete, "/api/v1/cart/items/rcb-flag");
        Assert.Empty((await removed.Content.ReadFromJsonAsync<CartDto>(TestClient.Json))!.Lines);
    }

    [Fact]
    public async Task Clear_cart_removes_every_item_and_a_retry_is_safe()
    {
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync("rcb-flag", 2);
        await customer.AddToCartOkAsync("csk-cap", 1);
        var key = TestClient.NewKey();

        var cleared = await customer.SendCartAsync(HttpMethod.Delete, "/api/v1/cart", key: key);
        var retried = await customer.SendCartAsync(HttpMethod.Delete, "/api/v1/cart", key: key);

        Assert.Empty((await cleared.Content.ReadFromJsonAsync<CartDto>(TestClient.Json))!.Lines);
        Assert.Equal("true", retried.Headers.GetValues(ApiHeaders.IdempotentReplay).Single());
        Assert.Empty((await customer.GetCartAsync()).Lines);
    }

    [Fact]
    public async Task Price_change_is_flagged_and_checkout_waits_until_the_new_total_is_accepted()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"reprice-{Guid.NewGuid():N}", 1000m, 10);
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync(product.Id, 2);

        await admin.SetPriceAsync(product.Id, 1100m);
        var flagged = await customer.GetCartAsync();
        Assert.True(flagged.RequiresReview);
        Assert.Equal((2000m, 2200m), (flagged.Total, flagged.TotalAfterAccept));
        Assert.Equal("cart-requires-review", await TestClient.CodeAsync(await customer.CheckoutAsync(Guid.NewGuid(), flagged.Total)));

        var accepted = (await (await customer.SendCartAsync(HttpMethod.Post, "/api/v1/cart/accept-changes")).Content.ReadFromJsonAsync<CartDto>(TestClient.Json))!;
        Assert.Equal(2200m, accepted.Total);
        Assert.Equal(HttpStatusCode.Created, (await customer.CheckoutAsync(Guid.NewGuid(), accepted.Total)).StatusCode);
    }
}
