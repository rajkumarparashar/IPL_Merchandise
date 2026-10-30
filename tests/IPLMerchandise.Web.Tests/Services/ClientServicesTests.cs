using IPLMerchandise.Web.Client.Auth;
using Microsoft.JSInterop;

namespace IPLMerchandise.Web.Tests.Services;

/// <summary>Regression: after a session expired, signing in again failed until a page refresh.</summary>
public class ClientServicesTests
{
    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(TValue)!);
    }

    [Fact]
    public async Task The_http_pipeline_sees_the_same_token_as_the_app_after_signing_in_again()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJSRuntime, StubJsRuntime>();
        ClientServices.Register(services, "http://api.test/");
        using var root = services.BuildServiceProvider();

        // HttpClientFactory resolves message handlers (AuthHeaderHandler) in a scope of its own.
        using var handlerScope = root.CreateScope();
        var appTokens = root.GetRequiredService<TokenStore>();
        var handlerTokens = handlerScope.ServiceProvider.GetRequiredService<TokenStore>();

        await handlerTokens.GetAsync();          // the handler reads (and caches) the token on its first request
        await appTokens.SetAsync("new-token");   // the user signs in again

        Assert.Same(appTokens, handlerTokens);
        Assert.Equal("new-token", await handlerTokens.GetAsync());
    }
}
