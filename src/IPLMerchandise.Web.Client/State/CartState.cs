using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Api;

namespace IPLMerchandise.Web.Client.State;

/// <summary>
/// Client-side owner of the cart. Guarantees for the UI:
/// <list type="bullet">
/// <item>Each user action gets one idempotency key. Automatic retries (RetryHandler) and the "Retry" button reuse it,
///   so the server applies the action at most once.</item>
/// <item>While an action is in flight, further actions are refused (double-click guard), on top of disabled buttons.</item>
/// </list>
/// </summary>
public sealed class CartState(ICartApi api)
{
    private sealed record PendingAction(string Description, string Key, Func<string, Task<ApiResult<CartDto>>> Send);

    private PendingAction? _failed;
    private int _version; // bumped by every load, change and sign-out: a background refresh that started earlier is discarded

    public CartDto? Cart { get; private set; }
    public bool IsBusy { get; private set; }
    public ApiProblem? LastProblem { get; private set; }

    /// <summary>The last action failed for a transient reason; <see cref="RetryAsync"/> will resend it with the same key.</summary>
    public bool CanRetry => _failed is not null;
    public string? FailedActionDescription => _failed?.Description;

    public int ItemCount => Cart?.ItemCount ?? 0;

    public int QuantityInCart(string productId) => Cart?.Lines.FirstOrDefault(l => l.ProductId == productId)?.Quantity ?? 0;

    /// <summary>
    /// How many more units the customer may add: limited by stock and the per-line cap (10, or 2 for limited editions),
    /// minus what is already in the cart. 0 means "nothing left to add", so the UI hides the add button.
    /// The API enforces the same rules; this only avoids offering an action that would be rejected.
    /// </summary>
    public int RemainingToAdd(ProductDto product) =>
        Math.Max(0, Math.Min(product.StockQuantity, ProductLimits.PerLine(product)) - QuantityInCart(product.Id));
    public event Action? Changed;

    public async Task<ApiResult<CartDto>> LoadAsync()
    {
        _version++;
        var result = await api.GetAsync();
        if (result.IsSuccess) Cart = result.Value;
        LastProblem = result.Problem;
        Notify();
        return result;
    }

    /// <summary>
    /// Background re-check of prices and stock (the server reviews the cart on every read). Silent: failures are ignored
    /// (the next check or the user's own action will surface them), and a result that arrives after the user changed the
    /// cart, or while a change is being saved, is dropped so it can never overwrite newer data.
    /// </summary>
    public async Task RefreshQuietlyAsync()
    {
        if (IsBusy || Cart is null) return; // not loaded yet, or signed out
        var version = _version;
        var result = await api.GetAsync();
        if (!result.IsSuccess || IsBusy || version != _version) return;
        Cart = result.Value;
        Notify();
    }

    public Task<ApiResult<CartDto>> AddAsync(string productId, string productName, int quantity) =>
        RunAsync(new PendingAction($"add {productName}", NewKey(), key => api.AddAsync(productId, quantity, key)));

    public Task<ApiResult<CartDto>> UpdateQuantityAsync(string productId, int quantity) =>
        RunAsync(new PendingAction("update quantity", NewKey(), key => api.UpdateAsync(productId, quantity, key)));

    public Task<ApiResult<CartDto>> RemoveAsync(string productId) =>
        RunAsync(new PendingAction("remove item", NewKey(), key => api.RemoveAsync(productId, key)));

    public Task<ApiResult<CartDto>> AcceptChangesAsync() =>
        RunAsync(new PendingAction("accept changes", NewKey(), key => api.AcceptChangesAsync(key)));

    /// <summary>Removes every item on the server (not to be confused with <see cref="Clear"/>, which forgets local state on sign-out).</summary>
    public Task<ApiResult<CartDto>> EmptyCartAsync() =>
        RunAsync(new PendingAction("clear cart", NewKey(), key => api.ClearAsync(key)));

    public Task<ApiResult<CartDto>> RetryAsync() =>
        _failed is { } failed ? RunAsync(failed) : Task.FromResult(ApiResult<CartDto>.Fail(new ApiProblem { Title = "Nothing to retry" }));

    public void Clear()
    {
        _version++;
        Cart = null;
        _failed = null;
        LastProblem = null;
        Notify();
    }

    private async Task<ApiResult<CartDto>> RunAsync(PendingAction action)
    {
        if (IsBusy)
            return ApiResult<CartDto>.Fail(new ApiProblem { Code = "busy", Title = "Please wait", Detail = "Your previous change is still being saved." });

        IsBusy = true;
        _version++;
        LastProblem = null;
        Notify();
        try
        {
            var result = await action.Send(action.Key);
            if (result.IsSuccess)
            {
                Cart = result.Value;
                _failed = null;
            }
            else
            {
                LastProblem = result.Problem;
                // Only transient failures are retried with the same key; a business rejection needs a new decision.
                _failed = result.Problem!.IsTransient ? action : null;
            }
            return result;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");
    private void Notify() => Changed?.Invoke();
}
