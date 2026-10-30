using IPLMerchandise.Application.Carts;
using IPLMerchandise.Application.Catalog;
using IPLMerchandise.Application.Checkout;
using IPLMerchandise.Application.Identity;
using IPLMerchandise.Application.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IPLMerchandise.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Abstractions.IProductChangeNotifier, Abstractions.NoProductChangeNotifier>(); // the API registers SignalR first
        services.AddScoped<CatalogService>();
        services.AddScoped<ProductAdminService>();
        services.AddScoped<CartService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<PendingCheckoutRecovery>();
        services.AddScoped<OrderService>();
        services.AddScoped<AuthService>();
        return services;
    }
}
