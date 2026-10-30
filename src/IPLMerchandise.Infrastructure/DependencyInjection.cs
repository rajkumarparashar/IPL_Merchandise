using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Infrastructure.Cosmos;
using IPLMerchandise.Infrastructure.Repositories;
using IPLMerchandise.Infrastructure.Security;
using IPLMerchandise.Infrastructure.Seeding;
using IPLMerchandise.Infrastructure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CosmosOptions>().Bind(configuration.GetSection(CosmosOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.Configure<AdminSeedOptions>(configuration.GetSection(AdminSeedOptions.Section));

        // One CosmosClient per process: it owns connection pools and caches and is thread-safe.
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<CosmosOptions>>().Value;
            return new CosmosClient(o.ConnectionString, new CosmosClientOptions
            {
                ApplicationName = "ipl-merchandise-api",
                ConnectionMode = o.UseGatewayMode ? ConnectionMode.Gateway : ConnectionMode.Direct,
                LimitToEndpoint = o.UseGatewayMode,
                // Consistency is left at the account default (Session). See DESIGN.md ADR-4.
                RequestTimeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds),
                MaxRetryAttemptsOnRateLimitedRequests = o.MaxRetryAttemptsOnThrottling,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(o.MaxRetryWaitSeconds),
                EnableContentResponseOnWrite = false,
                UseSystemTextJsonSerializerWithOptions = CosmosJson.Options
            });
        });

        services.AddSingleton<CosmosContainers>();
        services.AddSingleton<CosmosDbInitializer>();
        services.AddScoped<DataSeeder>();

        services.AddScoped<IProductRepository, CosmosProductRepository>();
        services.AddScoped<ICartRepository, CosmosCartRepository>();
        services.AddScoped<ICheckoutRepository, CosmosCheckoutRepository>();
        services.AddScoped<IOrderRepository, CosmosOrderRepository>();
        services.AddScoped<IUserRepository, CosmosUserRepository>();
        services.AddSingleton<IPasswordHasher, IdentityPasswordHasher>();

        // Product photos. Not part of the readiness check: the store keeps selling (with illustrations) if storage is down.
        services.AddOptions<ImageStorageOptions>().Bind(configuration.GetSection(ImageStorageOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ImageStorageOptions>>().Value;
            var clientOptions = new BlobClientOptions();
            clientOptions.Retry.MaxRetries = 3;
            clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
            return new BlobServiceClient(o.ConnectionString, clientOptions);
        });
        services.AddSingleton<IProductImageStore, BlobProductImageStore>();

        services.AddExceptionHandler<CosmosExceptionHandler>();
        services.AddHealthChecks().AddCheck<CosmosHealthCheck>("cosmos", tags: ["ready"]);
        return services;
    }
}
