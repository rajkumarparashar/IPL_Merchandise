using System.Threading.Channels;
using IPLMerchandise.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace IPLMerchandise.IntegrationTests;

/// <summary>Open carts hear about price and stock changes by push (SignalR), only for the products they hold.</summary>
[Collection(ApiCollection.Name)]
public class ProductUpdatesHubTests(ApiFactory factory)
{
    private async Task<(HubConnection Connection, Channel<string[]> Received)> WatchAsync(params string[] productIds)
    {
        var received = Channel.CreateUnbounded<string[]>();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/product-updates"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // the in-memory test server has no WebSockets
            })
            .Build();
        connection.On<string[]>("ProductsChanged", ids => received.Writer.TryWrite(ids));
        await connection.StartAsync();
        await connection.InvokeAsync("Watch", productIds);
        return (connection, received);
    }

    private static async Task<string[]> NextAsync(Channel<string[]> received)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await received.Reader.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task Price_changes_and_checkouts_are_pushed_to_watchers_of_that_product_only()
    {
        var admin = await TestClient.AdminAsync(factory);
        var watched = await admin.CreateProductAsync($"live-{Guid.NewGuid():N}", 300m, 10);
        var other = await admin.CreateProductAsync($"other-{Guid.NewGuid():N}", 300m, 10);
        var (connection, received) = await WatchAsync(watched.Id);
        await using var _ = connection;

        await admin.SetPriceAsync(other.Id, 320m); // not watched: nothing arrives
        await admin.SetPriceAsync(watched.Id, 350m);
        Assert.Equal([watched.Id], await NextAsync(received));

        var buyer = await TestClient.CustomerAsync(factory);
        await buyer.AddToCartOkAsync(watched.Id, 2);
        await buyer.CheckoutOkAsync();             // stock went down: watchers must re-check
        Assert.Equal([watched.Id], await NextAsync(received));
        Assert.False(received.Reader.TryRead(out var extra), $"unexpected push for {string.Join(",", extra ?? [])}");
    }

    [Fact]
    public async Task Watching_is_limited_to_a_cart_of_valid_product_ids()
    {
        var (connection, _) = await WatchAsync("csk-cap");
        await using var _c = connection;

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Watch", Enumerable.Range(0, 21).Select(i => $"p{i}").ToArray()));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Watch", new[] { "../../etc" }));
    }
}
