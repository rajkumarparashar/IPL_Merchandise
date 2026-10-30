using System.Collections.ObjectModel;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure.Cosmos;

/// <summary>
/// Creates the database and containers (idempotent). This is the single source of truth for partition keys and
/// indexing policy; scripts/provision-cosmos.sh creates the same layout in Azure.
/// </summary>
public sealed class CosmosDbInitializer(CosmosClient client, IOptions<CosmosOptions> options, ILogger<CosmosDbInitializer> logger)
{
    public static IReadOnlyList<ContainerProperties> ContainerDefinitions() =>
    [
        Catalog(),
        Simple(ContainerNames.Carts, "/customerId", defaultTtl: -1, indexNothing: true),
        Orders(),
        Simple(ContainerNames.Users, "/id", defaultTtl: null, indexNothing: true)
    ];

    public async Task InitializeAsync(CancellationToken ct)
    {
        var o = options.Value;
        var throughput = o.DatabaseAutoscaleMaxThroughput is { } max ? ThroughputProperties.CreateAutoscaleThroughput(max) : null;

        // The emulator (and Cosmos during a regional failover) may not be ready yet: retry with backoff instead of crashing.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var db = (await client.CreateDatabaseIfNotExistsAsync(o.DatabaseName, throughput, cancellationToken: ct)).Database;
                foreach (var definition in ContainerDefinitions())
                    await EnsureContainerAsync(db, definition, ct);
                logger.LogInformation("Cosmos database {Database} ready", o.DatabaseName);
                return;
            }
            catch (Exception ex) when (attempt < 30 && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 10));
                logger.LogWarning("Cosmos not reachable (attempt {Attempt}): {Error}. Retrying in {Delay}", attempt, ex.Message, delay);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Creates the container, or, if it already exists with an older indexing policy, replaces the policy so new queries
    /// (e.g. a new filter field) are served by the index. The partition key can never change in place (see DESIGN ADR-10).
    /// </summary>
    private async Task EnsureContainerAsync(Database db, ContainerProperties definition, CancellationToken ct)
    {
        var response = await db.CreateContainerIfNotExistsAsync(definition, cancellationToken: ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Created) return;

        var existing = response.Resource;
        if (PolicyKey(existing.IndexingPolicy) == PolicyKey(definition.IndexingPolicy)) return;

        existing.IndexingPolicy = definition.IndexingPolicy;
        await response.Container.ReplaceContainerAsync(existing, cancellationToken: ct);
        logger.LogInformation("Updated indexing policy of container {Container}", definition.Id);
    }

    /// <summary>
    /// Comparable form of a policy. Cosmos adds system paths of its own (e.g. /"_ts"/? and /"_etag"/?) to stored policies,
    /// so those are ignored; otherwise every start would see a "difference" and rewrite the policy.
    /// </summary>
    private static string PolicyKey(IndexingPolicy p)
    {
        static IEnumerable<string> UserPaths(IEnumerable<string> paths) => paths.Where(x => !x.StartsWith("/\"_", StringComparison.Ordinal)).Order();
        return string.Join(",", UserPaths(p.IncludedPaths.Select(x => x.Path))) + "|" +
               string.Join(",", UserPaths(p.ExcludedPaths.Select(x => x.Path))) + "|" +
               string.Join(";", p.CompositeIndexes.Select(c => string.Join(",", c.Select(x => $"{x.Path}:{x.Order}"))));
    }

    /// <summary>Only connectivity problems are worth waiting for. A 400 (bad policy) or 401 (bad key) must fail fast.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        CosmosException ce => ce.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.RequestTimeout
            or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.InternalServerError,
        HttpRequestException or TaskCanceledException => true,
        _ => false
    };

    // Indexing is opt-in: only the paths we filter or sort on are indexed. Writes cost fewer RUs because the large,
    // never-queried fields (descriptions, order snapshots, cart lines) are not indexed.

    private static ContainerProperties Catalog()
    {
        // TTL enabled (-1 = per-document opt-in) so completed checkout commits can expire.
        var props = new ContainerProperties(ContainerNames.Catalog, "/storeId") { DefaultTimeToLive = -1 };
        OptIn(props, "/storeId/?", "/docType/?", "/isActive/?", "/searchName/?", "/type/?", "/franchiseCode/?",
            "/createdAtUtc/?", "/productId/?", "/changedAtUtc/?", "/status/?", "/price/?", "/isFeatured/?");
        props.IndexingPolicy.CompositeIndexes.Add(Composite(("/createdAtUtc", CompositePathSortOrder.Ascending), ("/id", CompositePathSortOrder.Ascending)));
        props.IndexingPolicy.CompositeIndexes.Add(Composite(("/changedAtUtc", CompositePathSortOrder.Descending), ("/id", CompositePathSortOrder.Descending)));
        // Product sort orders: price (asc, and desc by reading the index in reverse) and name.
        props.IndexingPolicy.CompositeIndexes.Add(Composite(("/price", CompositePathSortOrder.Ascending), ("/id", CompositePathSortOrder.Ascending)));
        props.IndexingPolicy.CompositeIndexes.Add(Composite(("/searchName", CompositePathSortOrder.Ascending), ("/id", CompositePathSortOrder.Ascending)));
        return props;
    }

    private static ContainerProperties Orders()
    {
        var props = new ContainerProperties(ContainerNames.Orders, "/customerId");
        OptIn(props, "/customerId/?", "/docType/?", "/createdAtUtc/?");
        props.IndexingPolicy.CompositeIndexes.Add(Composite(("/createdAtUtc", CompositePathSortOrder.Descending), ("/id", CompositePathSortOrder.Descending)));
        return props;
    }

    /// <summary>Point-read-only containers: index nothing so writes are as cheap as possible.</summary>
    private static ContainerProperties Simple(string name, string partitionKey, int? defaultTtl, bool indexNothing)
    {
        var props = new ContainerProperties(name, partitionKey) { DefaultTimeToLive = defaultTtl };
        if (indexNothing) OptIn(props);
        return props;
    }

    private static void OptIn(ContainerProperties props, params string[] includedPaths)
    {
        props.IndexingPolicy.IncludedPaths.Clear();
        foreach (var path in includedPaths) props.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        props.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
    }

    private static Collection<CompositePath> Composite(params (string Path, CompositePathSortOrder Order)[] paths) =>
        new(paths.Select(p => new CompositePath { Path = p.Path, Order = p.Order }).ToList());
}
