using Microsoft.JSInterop;

namespace IPLMerchandise.Web.Client.Auth;

/// <summary>
/// Keeps the access token in browser storage. Trade-off (see DESIGN.md): simple and stateless, but readable by any
/// script on the page, so XSS would expose it. A backend-for-frontend with an HttpOnly cookie is the production upgrade.
/// </summary>
public sealed class TokenStore(IJSRuntime js)
{
    private const string Key = "ipl.accessToken";
    private string? _cached;
    private bool _loaded;

    public async ValueTask<string?> GetAsync()
    {
        if (_loaded) return _cached;
        try { _cached = await js.InvokeAsync<string?>("localStorage.getItem", Key); }
        catch (JSException) { _cached = null; }
        _loaded = true;
        return _cached;
    }

    public async ValueTask SetAsync(string? token)
    {
        _cached = token;
        _loaded = true;
        try
        {
            if (token is null) await js.InvokeVoidAsync("localStorage.removeItem", Key);
            else await js.InvokeVoidAsync("localStorage.setItem", Key, token);
        }
        catch (JSException)
        {
            // Storage unavailable (private mode): the session lasts until the tab closes.
        }
    }
}

/// <summary>Raised by the HTTP pipeline when the API rejects our token (expired or revoked).</summary>
public sealed class SessionEvents
{
    public event Action? SessionExpired;
    public void RaiseSessionExpired() => SessionExpired?.Invoke();
}
