using System.Net;
using System.Net.Http.Json;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.IntegrationTests.Infrastructure;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AdminApiTests(ApiFactory factory)
{
    [Fact]
    public async Task Only_admins_can_change_products()
    {
        var body = new CreateProductRequest { Name = "X", Type = ProductType.Cap, FranchiseCode = "MI", Price = 1m, StockQuantity = 1 };

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/products", body, TestClient.Json)).StatusCode);
        var customer = await TestClient.CustomerAsync(factory);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.Http.PostAsJsonAsync("/api/v1/products", body, TestClient.Json)).StatusCode);
    }

    [Fact]
    public async Task Update_with_a_stale_etag_is_412_and_is_not_applied()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"etag-{Guid.NewGuid():N}", 100m, 10);
        await admin.SetPriceAsync(product.Id, 120m); // product.ETag is now stale

        var stale = await admin.PutWithETagAsync($"/api/v1/products/{product.Id}/stock",
            new SetStockRequest { StockQuantity = 3, Reason = "Recount" }, product.ETag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency-conflict", await TestClient.CodeAsync(stale));
        Assert.Equal(10, (await admin.GetProductAsync(product.Id)).StockQuantity);

        var noEtag = await admin.PutWithETagAsync($"/api/v1/products/{product.Id}/stock",
            new SetStockRequest { StockQuantity = 3, Reason = "Recount" }, etag: null);
        Assert.Equal((HttpStatusCode)428, noEtag.StatusCode);
    }

    [Fact]
    public async Task Concurrent_updates_with_the_same_etag_only_one_succeeds()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"race-{Guid.NewGuid():N}", 100m, 10);

        var responses = await Task.WhenAll(Enumerable.Range(1, 5).Select(i =>
            admin.PutWithETagAsync($"/api/v1/products/{product.Id}/stock",
                new SetStockRequest { StockQuantity = 10 + i, Reason = $"Admin {i}" }, product.ETag)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
    }

    [Fact]
    public async Task Price_and_stock_changes_are_audited_with_who_old_new_and_reason()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"audit-{Guid.NewGuid():N}", 100m, 10);
        await admin.SetPriceAsync(product.Id, 150m);

        var audit = (await admin.Http.GetFromJsonAsync<PagedResult<AuditEntryDto>>($"/api/v1/products/{product.Id}/audit", TestClient.Json))!;

        var price = audit.Items.Single(a => a is { Field: AuditField.Price, Reason: AuditReason.ManualAdjustment });
        Assert.Equal((100m, 150m), (price.OldValue, price.NewValue));
        Assert.Contains(ApiFactory.AdminEmail, price.ChangedBy);
        Assert.Equal("Test price change", price.Note);
    }
}
