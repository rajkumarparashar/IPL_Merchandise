using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure.Cosmos;

/// <summary>Readiness: can we reach the database? Cheap metadata read, bounded by a short timeout.</summary>
public sealed class CosmosHealthCheck(CosmosClient client, IOptions<CosmosOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.GetDatabase(options.Value.DatabaseName).ReadAsync(cancellationToken: cts.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cosmos DB unreachable", ex);
        }
    }
}
