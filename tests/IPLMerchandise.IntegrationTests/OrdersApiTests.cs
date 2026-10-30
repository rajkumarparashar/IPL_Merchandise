using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class OrdersApiTests(ApiFactory factory)
{
    private async Task<(TestClient Customer, List<OrderDto> Orders)> CustomerWithOrders(int count)
    {
        var customer = await TestClient.CustomerAsync(factory);
        var orders = new List<OrderDto>();
        for (var i = 0; i < count; i++)
        {
            await customer.AddToCartOkAsync("rcb-flag", 1);
            orders.Add(await customer.CheckoutOkAsync());
        }
        return (customer, orders);
    }

    [Fact]
    public async Task History_is_newest_first_and_paged()
    {
        var (customer, orders) = await CustomerWithOrders(5);

        var ids = new List<Guid>();
        string? token = null;
        do
        {
            var url = "/api/v1/orders?pageSize=2" + (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var page = (await customer.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>(url, TestClient.Json))!;
            ids.AddRange(page.Items.Select(o => o.Id));
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(orders.Select(o => o.Id).Reverse(), ids);
    }

    [Fact]
    public async Task Date_range_filters_on_utc_instants()
    {
        var (customer, orders) = await CustomerWithOrders(1);
        var placed = new DateTimeOffset(orders[0].CreatedAtUtc).ToOffset(TimeSpan.FromHours(5.5)); // IST
        string Url(DateTimeOffset from, DateTimeOffset to) =>
            $"/api/v1/orders?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

        var inside = (await customer.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>(Url(placed.AddMinutes(-5), placed.AddMinutes(5)), TestClient.Json))!;
        var before = (await customer.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>(Url(placed.AddDays(-2), placed.AddDays(-1)), TestClient.Json))!;

        Assert.Single(inside.Items);
        Assert.Empty(before.Items);
    }

    [Fact]
    public async Task Summary_order_number_search_and_item_previews()
    {
        var (customer, orders) = await CustomerWithOrders(2);

        var stats = (await customer.Http.GetFromJsonAsync<OrderStatsDto>("/api/v1/orders/summary", TestClient.Json))!;
        Assert.Equal((2, 2 * orders[0].Total, 2 * orders[0].Total), (stats.TotalOrders, stats.SpentLast30Days, stats.TotalSpent));

        var shownNumber = "#IPL-" + orders[1].Id.ToString()[..8].ToUpperInvariant(); // what the UI displays
        var found = (await customer.Http.GetFromJsonAsync<PagedResult<OrderSummaryDto>>(
            $"/api/v1/orders?orderId={Uri.EscapeDataString(shownNumber)}", TestClient.Json))!;
        var match = Assert.Single(found.Items);
        Assert.Equal(orders[1].Id, match.Id);
        Assert.Equal(("RCB Flag", ProductType.Flag), (match.Items!.Single().ProductName, match.Items!.Single().Type));

        var bad = await customer.Http.GetAsync("/api/v1/orders?orderId=not-an-id!");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Customers_cannot_see_each_others_orders()
    {
        var (_, orders) = await CustomerWithOrders(1);
        var other = await TestClient.CustomerAsync(factory);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/orders/{orders[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task Expired_and_forged_tokens_are_rejected_with_401()
    {
        static string Token(string key, DateTime expires) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ipl-merchandise-api", Audience = "ipl-merchandise-web",
            Subject = new ClaimsIdentity([new Claim("sub", "someone"), new Claim("role", "Customer")]),
            IssuedAt = expires.AddHours(-1), NotBefore = expires.AddHours(-1), Expires = expires,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(ApiFactory.JwtKey, DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal("token-expired", await TestClient.CodeAsync(await client.GetAsync("/api/v1/orders")));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("attacker-key-attacker-key-attacker-key-123", DateTime.UtcNow.AddHours(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/orders")).StatusCode);
    }
}
