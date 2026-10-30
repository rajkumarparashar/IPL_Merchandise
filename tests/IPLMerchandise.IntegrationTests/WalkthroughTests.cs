using System.Net;
using System.Net.Http.Json;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.IntegrationTests.Infrastructure;

namespace IPLMerchandise.IntegrationTests;

/// <summary>Fresh database, so the seeded catalogue is exactly as in the assessment walkthrough.</summary>
public sealed class WalkthroughFactory : ApiFactory;

/// <summary>
/// The assessment's example walkthrough, step by step. Asha's checkout is processed first ("if Asha's is processed
/// first"); the truly simultaneous race is covered by CheckoutApiTests.Concurrent_checkouts_for_the_last_unit_never_oversell.
/// </summary>
public class WalkthroughTests(WalkthroughFactory factory) : IClassFixture<WalkthroughFactory>
{
    [Fact]
    public async Task Assessment_walkthrough()
    {
        var anon = factory.CreateClient();
        var asha = await TestClient.CustomerAsync(factory, "asha");
        var ravi = await TestClient.CustomerAsync(factory, "ravi");
        var admin = await TestClient.AdminAsync(factory);

        // Product list, page size 2: MI Jersey and CSK Cap, total > 2, link to the next page.
        var page = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?pageSize=2", TestClient.Json))!;
        Assert.Equal([("MI Jersey", 1999.00m), ("CSK Cap", 499.00m)], page.Items.Select(p => (p.Name, p.Price)));
        Assert.True(page.TotalCount > 2);
        Assert.NotNull(page.ContinuationToken);

        // Search type Cap + franchise CSK: one result, ₹499.00, in stock.
        var cap = Assert.Single((await anon.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?type=Cap&franchise=CSK", TestClient.Json))!.Items);
        Assert.Equal((499.00m, StockStatus.InStock), (cap.Price, cap.StockStatus));

        // Asha: 2 jerseys (₹3,998.00), 3 caps (₹5,495.00), 1 signed photo sent twice (added once, ₹10,494.00).
        Assert.Equal(3998.00m, (await asha.AddToCartOkAsync("mi-jersey-2026", 2)).Total);
        Assert.Equal(5495.00m, (await asha.AddToCartOkAsync("csk-cap", 3)).Total);
        var key = TestClient.NewKey();
        await asha.AddToCartAsync("kkr-signed-photo", 1, key);
        await asha.AddToCartAsync("kkr-signed-photo", 1, key);
        Assert.Equal(10494.00m, (await asha.GetCartAsync()).Total);

        // Ravi holds "the last" photo. Seed stock is 2, so (documented assumption) one sold elsewhere first.
        await ravi.AddToCartOkAsync("kkr-signed-photo", 1);
        await admin.SetStockAsync("kkr-signed-photo", 1);

        // Asha's checkout is processed first: confirmed for ₹10,494.00.
        var ashaOrderId = Guid.NewGuid();
        var ashaResponse = await asha.CheckoutAsync(ashaOrderId, 10494.00m);
        Assert.Equal(HttpStatusCode.Created, ashaResponse.StatusCode);
        Assert.Equal(10494.00m, (await ashaResponse.Content.ReadFromJsonAsync<OrderDto>(TestClient.Json))!.Total);

        // Ravi is told the photo is out of stock; no order, cart kept.
        var raviResponse = await ravi.CheckoutAsync(Guid.NewGuid(), 4999.00m);
        Assert.Equal(HttpStatusCode.Conflict, raviResponse.StatusCode);
        Assert.Equal("checkout-rejected", await TestClient.CodeAsync(raviResponse));
        Assert.Single((await ravi.GetCartAsync()).Lines);

        // Stock: MI Jersey 48, CSK Cap 97, KKR Signed Photo 0.
        Assert.Equal((48, 97, 0), (
            (await admin.GetProductAsync("mi-jersey-2026")).StockQuantity,
            (await admin.GetProductAsync("csk-cap")).StockQuantity,
            (await admin.GetProductAsync("kkr-signed-photo")).StockQuantity));

        // Asha's checkout retried with the same order id: same order, stock unchanged.
        var retry = await asha.CheckoutAsync(ashaOrderId, 10494.00m);
        Assert.Equal((HttpStatusCode.OK, ashaOrderId), (retry.StatusCode, (await retry.Content.ReadFromJsonAsync<OrderDto>(TestClient.Json))!.Id));
        Assert.Equal(97, (await admin.GetProductAsync("csk-cap")).StockQuantity);

        // Admin raises the cap to ₹549.00: Asha's history still shows ₹10,494.00 with caps at ₹499.00.
        await admin.SetPriceAsync("csk-cap", 549.00m);
        var summary = Assert.Single((await asha.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>("/api/v1/orders", TestClient.Json))!.Items);
        Assert.Equal(10494.00m, summary.Total);
        var order = (await asha.Http.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{summary.Id}", TestClient.Json))!;
        Assert.Equal(499.00m, order.Lines.Single(l => l.ProductId == "csk-cap").UnitPrice);
        Assert.Equal(549.00m, (await anon.GetFromJsonAsync<ProductDto>("/api/v1/products/csk-cap", TestClient.Json))!.Price);
    }
}
