using Bunit.TestDoubles;
using IPLMerchandise.Web.Client.Api;
using IPLMerchandise.Web.Client.Auth;
using IPLMerchandise.Web.Client.Formatting;
using IPLMerchandise.Web.Client.State;
using IPLMerchandise.Web.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace IPLMerchandise.Web.Tests;

/// <summary>bUnit context with MudBlazor, fake API clients and the real client-side state services.</summary>
public abstract class StoreTestContext : TestContext
{
    protected FakeCatalogApi Catalog { get; } = new();
    protected FakeCartApi CartApi { get; } = new();
    protected FakeOrdersApi OrdersApi { get; } = new();
    protected FakeAuthApi AuthApi { get; } = new();
    protected FakeAdminApi AdminApi { get; } = new();
    protected FakeProductUpdates ProductUpdates { get; } = new();
    protected TestAuthorizationContext Auth { get; }

    protected StoreTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; // localStorage / sessionStorage / MudBlazor JS calls return defaults
        Services.AddMudServices();
        Services.AddSingleton<ICatalogApi>(Catalog);
        Services.AddSingleton<ICartApi>(CartApi);
        Services.AddSingleton<IOrdersApi>(OrdersApi);
        Services.AddSingleton<IAuthApi>(AuthApi);
        Services.AddSingleton<IAdminApi>(AdminApi);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<TokenStore>();
        Services.AddSingleton<SessionEvents>();
        Services.AddSingleton<JwtAuthStateProvider>();
        Services.AddSingleton<CartState>();
        Services.AddSingleton<IPLMerchandise.Web.Client.State.IProductUpdates>(ProductUpdates);
        Services.AddSingleton<CheckoutSession>();
        Services.AddSingleton<UserTimeZone>();
        Auth = this.AddTestAuthorization();
        RenderComponent<MudPopoverProvider>();
    }

    protected void SignInAs(params string[] roles)
    {
        Auth.SetAuthorized("Asha");
        Auth.SetRoles(roles);
    }

    protected CartState CartState => Services.GetRequiredService<CartState>();
}
