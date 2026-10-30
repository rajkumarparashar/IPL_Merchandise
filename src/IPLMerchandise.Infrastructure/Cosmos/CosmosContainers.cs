using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure.Cosmos;

/// <summary>Container names and partition keys. See DESIGN.md, ADR-3, for why each key was chosen.</summary>
public static class ContainerNames
{
    /// <summary>Products, price/stock audit, checkout commits. PK /storeId → all stock changes share one partition, enabling one transaction.</summary>
    public const string Catalog = "catalog";

    /// <summary>One cart per customer plus its idempotency records. PK /customerId.</summary>
    public const string Carts = "carts";

    /// <summary>Orders. PK /customerId → history is a single-partition query.</summary>
    public const string Orders = "orders";

    /// <summary>Accounts. PK /id (hash of normalised email) → login is a point read.</summary>
    public const string Users = "users";
}

public sealed class CosmosContainers(CosmosClient client, IOptions<CosmosOptions> options)
{
    private readonly Database _db = client.GetDatabase(options.Value.DatabaseName);

    public CosmosClient Client => client;
    public string StoreId { get; } = options.Value.StoreId;
    public Container Catalog => _db.GetContainer(ContainerNames.Catalog);
    public Container Carts => _db.GetContainer(ContainerNames.Carts);
    public Container Orders => _db.GetContainer(ContainerNames.Orders);
    public Container Users => _db.GetContainer(ContainerNames.Users);
}
