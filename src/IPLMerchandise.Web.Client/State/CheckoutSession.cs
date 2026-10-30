using Microsoft.JSInterop;

namespace IPLMerchandise.Web.Client.State;

/// <summary>
/// The order ID for the checkout in progress. Created once, reused for every retry (button, automatic, or after a page
/// reload), and cleared only when the order is confirmed. That is what makes "place order" safe to press again.
/// </summary>
public sealed class CheckoutSession(IJSRuntime js)
{
    private const string Key = "ipl.pendingOrderId";
    private Guid? _orderId;

    public async Task<Guid> GetOrCreateOrderIdAsync()
    {
        if (_orderId is { } id) return id;
        try
        {
            var stored = await js.InvokeAsync<string?>("sessionStorage.getItem", Key);
            if (Guid.TryParse(stored, out var parsed)) return (_orderId = parsed).Value;
        }
        catch (JSException) { }

        _orderId = Guid.NewGuid();
        try { await js.InvokeVoidAsync("sessionStorage.setItem", Key, _orderId.Value.ToString()); } catch (JSException) { }
        return _orderId.Value;
    }

    public async Task CompleteAsync()
    {
        _orderId = null;
        try { await js.InvokeVoidAsync("sessionStorage.removeItem", Key); } catch (JSException) { }
    }
}
