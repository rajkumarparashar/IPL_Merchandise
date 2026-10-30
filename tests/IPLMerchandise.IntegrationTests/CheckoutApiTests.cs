using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Checkout;
using IPLMerchandise.Contracts.V1;
using ChangeContext = IPLMerchandise.Domain.Catalog.ChangeContext;
using ChangeReason = IPLMerchandise.Domain.Catalog.ChangeReason;
using IPLMerchandise.Domain.Orders;
using IPLMerchandise.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class CheckoutApiTests(ApiFactory factory)
{
    private async Task<int> StockOf(string id) => (await factory.CreateClient().GetFromJsonAsync<ProductDto>($"/api/v1/products/{id}", TestClient.Json))!.StockQuantity;

    private static async Task<int> OrderCount(TestClient customer) =>
        (await customer.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>("/api/v1/orders", TestClient.Json))!.Items.Count;

    [Fact]
    public async Task Retried_checkout_returns_the_same_order_and_deducts_stock_once()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"retry-{Guid.NewGuid():N}", 500m, 10);
        var customer = await TestClient.CustomerAsync(factory);
        var cart = await customer.AddToCartOkAsync(product.Id, 2);
        var orderId = Guid.NewGuid();

        var first = await customer.CheckoutAsync(orderId, cart.Total);
        var retry = await customer.CheckoutAsync(orderId, cart.Total);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(orderId, (await retry.Content.ReadFromJsonAsync<OrderDto>(TestClient.Json))!.Id);
        Assert.Equal(8, await StockOf(product.Id));
        Assert.Equal(1, await OrderCount(customer));
    }

    [Fact]
    public async Task Simultaneous_checkouts_with_the_same_order_id_create_one_order()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"burst-{Guid.NewGuid():N}", 500m, 10);
        var customer = await TestClient.CustomerAsync(factory);
        var cart = await customer.AddToCartOkAsync(product.Id, 3);
        var orderId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => customer.CheckoutAsync(orderId, cart.Total)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, await StockOf(product.Id));
        Assert.Equal(1, await OrderCount(customer));
    }

    [Fact]
    public async Task Concurrent_checkouts_for_the_last_unit_never_oversell()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"last-{Guid.NewGuid():N}", 4999m, 1, ProductType.SignedPhotograph, "KKR");
        var buyers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => TestClient.CustomerAsync(factory, $"buyer{i}")));
        foreach (var b in buyers) await b.AddToCartOkAsync(product.Id, 1);

        var responses = await Task.WhenAll(buyers.Select(b => b.CheckoutAsync(Guid.NewGuid(), 4999m)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(0, await StockOf(product.Id));
    }

    [Fact]
    public async Task Rejected_order_deducts_nothing_names_the_failing_line_and_keeps_the_cart()
    {
        var admin = await TestClient.AdminAsync(factory);
        var plenty = await admin.CreateProductAsync($"plenty-{Guid.NewGuid():N}", 100m, 50);
        var scarce = await admin.CreateProductAsync($"scarce-{Guid.NewGuid():N}", 200m, 3);
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync(plenty.Id, 5);
        var cart = await customer.AddToCartOkAsync(scarce.Id, 3);
        await admin.SetStockAsync(scarce.Id, 1); // someone else bought two meanwhile

        var response = await customer.CheckoutAsync(Guid.NewGuid(), cart.Total);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var failing = (await TestClient.ProblemAsync(response)).GetProperty("failingLines").Deserialize<List<CheckoutFailureLine>>(TestClient.Json)!;
        Assert.Equal((scarce.Id, 1), (failing.Single().ProductId, failing.Single().Available));
        Assert.Equal(50, await StockOf(plenty.Id));   // the good line was not deducted either
        Assert.Equal(2, (await customer.GetCartAsync()).Lines.Count);
        Assert.Equal(0, await OrderCount(customer));
    }

    [Fact]
    public async Task Interrupted_checkout_is_completed_by_recovery_without_deducting_twice()
    {
        var admin = await TestClient.AdminAsync(factory);
        var created = await admin.CreateProductAsync($"crash-{Guid.NewGuid():N}", 600m, 10);
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync(created.Id, 2);
        var orderId = Guid.NewGuid();

        // Simulate a crash: the stock transaction committed, but the process died before writing the order.
        using (var scope = factory.Services.CreateScope())
        {
            var product = (await scope.ServiceProvider.GetRequiredService<IProductRepository>().GetAsync(created.Id, default))!;
            var placedAt = DateTime.UtcNow.AddMinutes(-5); // older than the recovery grace period
            var order = Order.Place(orderId, customer.UserId, [new OrderLine(product.Id, product.Name, product.FranchiseCode, product.Price, 2)], placedAt);
            var audit = product.DeductStock(2, new ChangeContext("test", ChangeReason.Order, placedAt, OrderId: orderId.ToString("D")));
            var result = await scope.ServiceProvider.GetRequiredService<ICheckoutRepository>()
                .CommitAsync([product], [audit], new CheckoutCommit(order, CommitStatus.Pending), default);
            Assert.Equal(CommitResult.Committed, result);
        }
        Assert.Equal(0, await OrderCount(customer));

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<PendingCheckoutRecovery>().RecoverAsync(default);

        Assert.Equal(1, await OrderCount(customer));
        Assert.Empty((await customer.GetCartAsync()).Lines);
        var retry = await customer.CheckoutAsync(orderId, 1200m); // the customer's own retry just gets the order back
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(8, await StockOf(created.Id));
    }

    [Fact]
    public async Task Shipping_address_is_validated_and_stored_on_the_order()
    {
        var customer = await TestClient.CustomerAsync(factory);
        var cart = await customer.AddToCartOkAsync("rcb-flag", 3);
        var address = new ShippingAddressDto
        {
            FullName = "Raj Kumar", Mobile = "9876543210", Line1 = "H No 12-6-87, Green Park", Line2 = "Miyapur",
            City = "Hyderabad", State = "Telangana", PinCode = "500049"
        };

        var badPin = await customer.Http.PostAsJsonAsync("/api/v1/checkout",
            new CheckoutRequest { OrderId = Guid.NewGuid(), ExpectedTotal = cart.Total, ShippingAddress = address with { PinCode = "05004" } }, TestClient.Json);
        var badState = await customer.Http.PostAsJsonAsync("/api/v1/checkout",
            new CheckoutRequest { OrderId = Guid.NewGuid(), ExpectedTotal = cart.Total, ShippingAddress = address with { State = "Atlantis" } }, TestClient.Json);
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (badPin.StatusCode, badState.StatusCode));

        var placed = await customer.Http.PostAsJsonAsync("/api/v1/checkout",
            new CheckoutRequest { OrderId = Guid.NewGuid(), ExpectedTotal = cart.Total, ShippingAddress = address }, TestClient.Json);
        var order = (await placed.Content.ReadFromJsonAsync<OrderDto>(TestClient.Json))!;
        var stored = (await customer.Http.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{order.Id}", TestClient.Json))!;

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        Assert.Equal(address, stored.ShippingAddress);
        Assert.Equal(Contracts.V1.PaymentMethod.CashOnDelivery, stored.PaymentMethod);
    }

    [Fact]
    public async Task Order_keeps_the_price_paid_after_a_price_change()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"snap-{Guid.NewGuid():N}", 499m, 10, ProductType.Cap, "CSK");
        var customer = await TestClient.CustomerAsync(factory);
        await customer.AddToCartOkAsync(product.Id, 3);
        var order = await customer.CheckoutOkAsync();

        await admin.SetPriceAsync(product.Id, 549m);

        var stored = (await customer.Http.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{order.Id}", TestClient.Json))!;
        Assert.Equal((499m, 1497m), (stored.Lines.Single().UnitPrice, stored.Total));
    }
}
