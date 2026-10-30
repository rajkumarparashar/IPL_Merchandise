using System.Text.RegularExpressions;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Carts;
using Microsoft.AspNetCore.SignalR;

namespace IPLMerchandise.API.Realtime;

/// <summary>
/// Push channel for product changes. A cart or checkout page calls <see cref="Watch"/> with the ids in its cart and gets a
/// "ProductsChanged" message when any of them changes. Anonymous on purpose: it carries only product ids (public data),
/// never prices or anyone's cart, and the client re-reads its cart through the authenticated API.
/// </summary>
public sealed partial class ProductUpdatesHub : Hub
{
    public const string Path = "/hubs/product-updates";
    public const string ProductsChangedMessage = "ProductsChanged";
    private const string WatchedKey = "watched";

    public static string GroupFor(string productId) => "product:" + productId;

    /// <summary>Replaces the set of products this connection follows (at most a cart's worth).</summary>
    public async Task Watch(string[] productIds)
    {
        if (productIds.Length > CartRules.MaxLines || productIds.Any(id => !SafeId().IsMatch(id ?? "")))
            throw new HubException($"Send at most {CartRules.MaxLines} valid product ids.");

        var previous = Context.Items.TryGetValue(WatchedKey, out var value) && value is HashSet<string> set ? set : [];
        var next = productIds.ToHashSet(StringComparer.Ordinal);
        foreach (var id in previous.Except(next)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(id));
        foreach (var id in next.Except(previous)) await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(id));
        Context.Items[WatchedKey] = next;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}

/// <summary>Sends "these products changed" to the connections watching them. Never throws: a failed push only means a later check.</summary>
public sealed class SignalRProductChangeNotifier(IHubContext<ProductUpdatesHub> hub, ILogger<SignalRProductChangeNotifier> logger)
    : IProductChangeNotifier
{
    public async Task ProductsChangedAsync(IReadOnlyCollection<string> productIds)
    {
        if (productIds.Count == 0) return;
        try
        {
            var ids = productIds.Distinct().ToArray();
            await hub.Clients.Groups(ids.Select(ProductUpdatesHub.GroupFor).ToList())
                .SendAsync(ProductUpdatesHub.ProductsChangedMessage, ids, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not push product change notification for {ProductCount} products", productIds.Count);
        }
    }
}
