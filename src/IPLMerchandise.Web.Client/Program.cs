using IPLMerchandise.Web.Client.Api;
using IPLMerchandise.Web.Client.Auth;
using IPLMerchandise.Web.Client.Formatting;
using IPLMerchandise.Web.Client.State;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
ClientServices.Register(builder.Services, await ResolveApiBaseUrlAsync(builder));
await builder.Build().RunAsync();

// The API address is runtime configuration served by the host (per environment), not baked into the WebAssembly build.
static async Task<string> ResolveApiBaseUrlAsync(WebAssemblyHostBuilder builder)
{
    try
    {
        using var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromSeconds(10) };
        var config = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<ClientConfig>(http, "client-config");
        if (!string.IsNullOrWhiteSpace(config?.ApiBaseUrl)) return config.ApiBaseUrl;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
    {
        // Fall through: same-origin deployment behind a reverse proxy.
    }
    return builder.HostEnvironment.BaseAddress;
}

internal sealed record ClientConfig(string? ApiBaseUrl);

public static class ClientServices
{
    public static void Register(IServiceCollection services, string apiBaseUrl)
    {
        services.AddMudServices(o => o.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight);
        services.AddSingleton(TimeProvider.System);

        // Auth: the token lives in browser storage; the provider turns it into the UI's ClaimsPrincipal.
        services.AddAuthorizationCore();
        services.AddCascadingAuthenticationState();
        // Singleton on purpose: HttpClientFactory builds handlers (AuthHeaderHandler) in their own DI scope. A scoped
        // TokenStore gave that handler a separate instance that kept a cached, expired token after re-login, so every
        // request after signing in again was rejected until a page refresh. One instance per browser tab is correct in WASM.
        services.AddSingleton<TokenStore>();
        services.AddSingleton<SessionEvents>();
        services.AddScoped<JwtAuthStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());

        // HTTP pipeline, outermost first: one correlation id per logical call → retries → bearer token.
        services.AddTransient<CorrelationIdHandler>();
        services.AddTransient<RetryHandler>();
        services.AddTransient<AuthHeaderHandler>();

        AddApi<ICatalogApi, CatalogApi>(services, apiBaseUrl);
        AddApi<ICartApi, CartApi>(services, apiBaseUrl);
        AddApi<IOrdersApi, OrdersApi>(services, apiBaseUrl);
        AddApi<IAuthApi, AuthApi>(services, apiBaseUrl);
        AddApi<IAdminApi, AdminApi>(services, apiBaseUrl);

        // One live connection per tab for "product changed" pushes (cart and checkout pages).
        services.AddSingleton<IProductUpdates>(sp => new SignalRProductUpdates(
            (apiBaseUrl.EndsWith('/') ? apiBaseUrl : apiBaseUrl + "/") + "hubs/product-updates",
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SignalRProductUpdates>>()));
        services.AddScoped<CartState>();
        services.AddScoped<CheckoutSession>();
        services.AddScoped<UserTimeZone>();
    }

    private static void AddApi<TInterface, TImplementation>(IServiceCollection services, string baseUrl)
        where TInterface : class where TImplementation : class, TInterface =>
        services.AddHttpClient<TInterface, TImplementation>(c =>
            {
                c.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
                c.Timeout = TimeSpan.FromSeconds(20); // whole logical call, including retries
            })
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddHttpMessageHandler<RetryHandler>()
            .AddHttpMessageHandler<AuthHeaderHandler>();
}
