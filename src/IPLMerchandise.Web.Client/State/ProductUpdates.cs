using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Web.Client.State;

/// <summary>Pushes from the API when products change (price, stock, availability). See DESIGN.md ADR-12.</summary>
public interface IProductUpdates
{
    /// <summary>Some watched products changed; the ids are passed.</summary>
    event Action<IReadOnlyList<string>>? ProductsChanged;

    /// <summary>The connection dropped and came back: pushes may have been missed, so re-check.</summary>
    event Action? Reconnected;

    /// <summary>Replaces the set of products to hear about. An empty set leaves every group (the connection stays open).</summary>
    Task WatchAsync(IReadOnlyCollection<string> productIds);
}

/// <summary>
/// One SignalR connection per browser tab, opened the first time a page watches something. If it can't connect,
/// nothing breaks: the cart is still re-checked when the customer returns to the tab and, finally, at checkout.
/// </summary>
public sealed class SignalRProductUpdates(string hubUrl, ILogger<SignalRProductUpdates> logger) : IProductUpdates, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _connection;
    private string[] _watched = [];

    public event Action<IReadOnlyList<string>>? ProductsChanged;
    public event Action? Reconnected;

    public async Task WatchAsync(IReadOnlyCollection<string> productIds)
    {
        _watched = productIds.Distinct().Order(StringComparer.Ordinal).ToArray();
        await _gate.WaitAsync();
        try
        {
            if (_connection is null && _watched.Length == 0) return; // nothing to follow: don't open a connection
            var connection = Connection();
            if (connection.State == HubConnectionState.Disconnected) await connection.StartAsync();
            if (connection.State == HubConnectionState.Connected) await connection.InvokeAsync("Watch", _watched);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live product updates unavailable; changes will be picked up on the next check");
        }
        finally
        {
            _gate.Release();
        }
    }

    private HubConnection Connection()
    {
        if (_connection is not null) return _connection;
        var connection = new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect().Build();
        connection.On<string[]>("ProductsChanged", ids => ProductsChanged?.Invoke(ids));
        connection.Reconnected += async _ =>
        {
            // A new connection has no groups: follow the same products again, then let pages catch up.
            await connection.InvokeAsync("Watch", _watched);
            Reconnected?.Invoke();
        };
        return _connection = connection;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
