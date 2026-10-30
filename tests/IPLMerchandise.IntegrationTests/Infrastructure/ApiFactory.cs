using Azure.Storage.Blobs;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IPLMerchandise.IntegrationTests.Infrastructure;

/// <summary>
/// Runs the real API in-process against the Cosmos DB emulator and Azurite, in a throwaway database and blob container
/// that are deleted afterwards. Set COSMOS_CONNECTION_STRING / AZURITE_CONNECTION_STRING to use other instances.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string EmulatorConnectionString =
        "AccountEndpoint=http://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;";

    /// <summary>Azurite's well-known development account (public, not a secret).</summary>
    public const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    public static string StorageConnectionString => Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING") ?? AzuriteConnectionString;

    public const string AdminEmail = "admin@tests.local";
    public const string AdminPassword = "Admin@12345";
    public const string JwtKey = "integration-tests-signing-key-0123456789abcdef";

    public string DatabaseName { get; } = $"ipl-tests-{Guid.NewGuid():N}"[..24];
    public string ImageContainerName { get; } = $"ipl-tests-{Guid.NewGuid():N}"[..24];

    protected virtual IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Cosmos:ConnectionString"] = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING") ?? EmulatorConnectionString,
                ["Cosmos:DatabaseName"] = DatabaseName,
                ["Cosmos:UseGatewayMode"] = "true",
                ["Cosmos:InitializeOnStartup"] = "true",
                ["Cosmos:SeedOnStartup"] = "true",
                ["ImageStorage:ConnectionString"] = StorageConnectionString,
                ["ImageStorage:ContainerName"] = ImageContainerName,
                ["ImageStorage:CreateContainerIfMissing"] = "true",
                ["Jwt:SigningKey"] = JwtKey,
                ["AdminSeed:Email"] = AdminEmail,
                ["AdminSeed:Password"] = AdminPassword,
                ["RateLimiting:SearchPerMinute"] = "10000",
                ["RateLimiting:CheckoutPerMinute"] = "10000",
                ["RateLimiting:AuthPerMinute"] = "10000",
                ["Checkout:RecoveryIntervalSeconds"] = "0",
                ["Logging:LogLevel:Default"] = "Warning"
            };
            foreach (var (k, v) in ExtraSettings) settings[k] = v;
            config.AddInMemoryCollection(settings);
        });
    }

    public async Task InitializeAsync()
    {
        await EnsureEmulatorReachableAsync();
        await EnsureAzuriteReachableAsync();
        _ = Server; // start the host: creates the database, containers and seed data
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await Services.GetRequiredService<CosmosClient>().GetDatabase(DatabaseName).DeleteAsync();
        }
        catch (CosmosException)
        {
            // best effort cleanup
        }
        try
        {
            await Services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(ImageContainerName).DeleteIfExistsAsync();
        }
        catch (Azure.RequestFailedException)
        {
            // best effort cleanup
        }
        await base.DisposeAsync();
    }

    private static async Task EnsureAzuriteReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await new BlobServiceClient(StorageConnectionString).GetPropertiesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Integration tests need Azurite (Azure Storage emulator) at 127.0.0.1:10000. Start it with `docker compose up -d azurite` " +
                "(see README → Running tests) or set AZURITE_CONNECTION_STRING.", ex);
        }
    }

    private static async Task EnsureEmulatorReachableAsync()
    {
        var cs = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING") ?? EmulatorConnectionString;
        var endpoint = cs.Split(';').First(p => p.StartsWith("AccountEndpoint=")).Split('=', 2)[1];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            await http.GetAsync(endpoint);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Integration tests need the Cosmos DB emulator at {endpoint}. Start it with `docker compose up -d cosmos` " +
                "(see README → Running tests) or set COSMOS_CONNECTION_STRING.", ex);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
