using System.Net;
using System.Text;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;

namespace IPLMerchandise.Infrastructure.Repositories;

/// <summary>All queries are scoped to the store's single logical partition: no fan-out, predictable RU cost.</summary>
public sealed class CosmosProductRepository(CosmosContainers cosmos) : IProductRepository
{
    private Container Container => cosmos.Catalog;
    private string StoreId => cosmos.StoreId;

    public async Task<Product?> GetAsync(string id, CancellationToken ct)
    {
        var doc = await CosmosQuery.ReadOrDefaultAsync<ProductDocument>(Container, id, StoreId, ct);
        return doc?.DocType == DocTypes.Product ? doc.ToDomain() : null;
    }

    public async Task<IReadOnlyDictionary<string, Product>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<string, Product>();
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.storeId = @storeId AND c.docType = @docType AND ARRAY_CONTAINS(@ids, c.id)")
            .WithParameter("@storeId", StoreId)
            .WithParameter("@docType", DocTypes.Product)
            .WithParameter("@ids", ids.ToArray());
        var docs = await CosmosQuery.ReadAsync<ProductDocument>(Container, query, StoreId, ids.Count, ct);
        return docs.ToDictionary(d => d.Id, d => d.ToDomain());
    }

    public async Task<Page<Product>> SearchAsync(ProductSearchCriteria criteria, ProductSortOrder sort, ProductCursor? after, int pageSize, CancellationToken ct)
    {
        if (sort == ProductSortOrder.Featured)
            return await SearchFeaturedFirstAsync(criteria, after, pageSize, ct);

        // Every order ends with id as a tie-breaker, so the order is total and keyset paging never skips or repeats.
        // Each needs a composite index (see CosmosDbInitializer); a descending order uses the ascending index in reverse.
        var (orderBy, keyset, key) = sort switch
        {
            ProductSortOrder.PriceAscending => ("c.price ASC, c.id ASC", "(c.price > @after OR (c.price = @after AND c.id > @afterId))", (object?)after?.Price),
            ProductSortOrder.PriceDescending => ("c.price DESC, c.id DESC", "(c.price < @after OR (c.price = @after AND c.id < @afterId))", after?.Price),
            _ => ("c.searchName ASC, c.id ASC", "(c.searchName > @after OR (c.searchName = @after AND c.id > @afterId))", after?.SearchName)
        };
        var docs = await QueryPageAsync(criteria, segment: null, orderBy, after is null ? null : (keyset, key!, after.Id), pageSize + 1, ct);
        return new Page<Product>(docs.Take(pageSize).Select(d => d.ToDomain()).ToList(), docs.Count > pageSize);
    }

    // The "Featured" order is two keyset segments read one after the other: featured products, then the rest, each by
    // (createdAtUtc, id). This keeps each query a simple single-partition range on an existing composite index, and
    // the second segment also matches products saved before the isFeatured field existed.
    private const string FeaturedSegment = "c.isFeatured = true";
    private const string RestSegment = "(NOT IS_DEFINED(c.isFeatured) OR c.isFeatured = false)";
    private const string ByCreated = "c.createdAtUtc ASC, c.id ASC";
    private const string AfterCreated = "(c.createdAtUtc > @after OR (c.createdAtUtc = @after AND c.id > @afterId))";

    private async Task<Page<Product>> SearchFeaturedFirstAsync(ProductSearchCriteria criteria, ProductCursor? after, int pageSize, CancellationToken ct)
    {
        var wanted = pageSize + 1; // one extra row tells us whether another page exists
        var docs = new List<ProductDocument>();
        var stillInFeatured = after is null || after.Featured;

        if (stillInFeatured)
            docs.AddRange(await QueryPageAsync(criteria, FeaturedSegment, ByCreated, KeysetAfterCreated(after), wanted, ct));

        if (docs.Count < wanted && !criteria.FeaturedOnly)
        {
            // Coming from the featured segment, the rest starts at its beginning; otherwise continue after the cursor.
            var restCursor = stillInFeatured ? null : KeysetAfterCreated(after);
            docs.AddRange(await QueryPageAsync(criteria, RestSegment, ByCreated, restCursor, wanted - docs.Count, ct));
        }
        return new Page<Product>(docs.Take(pageSize).Select(d => d.ToDomain()).ToList(), docs.Count > pageSize);
    }

    private static (string Keyset, object Key, string Id)? KeysetAfterCreated(ProductCursor? after) =>
        after is null ? null : (AfterCreated, CosmosJson.Format(after.CreatedAtUtc), after.Id);

    private async Task<List<ProductDocument>> QueryPageAsync(ProductSearchCriteria criteria, string? segment, string orderBy,
        (string Keyset, object Key, string Id)? after, int take, CancellationToken ct)
    {
        var (where, parameters) = BuildFilter(criteria);
        if (segment is not null) where.Append(" AND ").Append(segment);
        if (after is { } a)
        {
            // Keyset pagination: continue strictly after the last item seen. Cost is independent of how deep we page.
            where.Append(" AND ").Append(a.Keyset);
            parameters["@after"] = a.Key;
            parameters["@afterId"] = a.Id;
        }
        var sql = $"SELECT TOP {take} * FROM c WHERE {where} ORDER BY {orderBy}";
        return await CosmosQuery.ReadAsync<ProductDocument>(Container, ToQuery(sql, parameters), StoreId, take, ct);
    }

    public async Task<long> CountAsync(ProductSearchCriteria criteria, CancellationToken ct)
    {
        var (where, parameters) = BuildFilter(criteria);
        var result = await CosmosQuery.ReadAsync<long>(Container, ToQuery($"SELECT VALUE COUNT(1) FROM c WHERE {where}", parameters), StoreId, 1, ct);
        return result.FirstOrDefault();
    }

    /// <summary>
    /// Projects only type and franchise of matching products and lets the caller count them. One single-partition
    /// query with a tiny payload: fine for a store catalogue. At thousands of SKUs, switch to GROUP BY or a
    /// precomputed facet document maintained from the change feed.
    /// </summary>
    public async Task<IReadOnlyList<(ProductType Type, string FranchiseCode)>> GetFacetKeysAsync(string? nameContains, CancellationToken ct)
    {
        var (where, parameters) = BuildFilter(new ProductSearchCriteria(nameContains, [], [], IncludeInactive: false));
        var rows = await CosmosQuery.ReadAsync<FacetRow>(Container,
            ToQuery($"SELECT c.type, c.franchiseCode FROM c WHERE {where}", parameters), StoreId, 10_000, ct);
        return rows.Select(r => (r.Type, r.FranchiseCode)).ToList();
    }

    private sealed class FacetRow
    {
        public ProductType Type { get; set; }
        public string FranchiseCode { get; set; } = "";
    }

    public async Task<Product> CreateAsync(Product product, IReadOnlyList<PriceStockChange> audit, CancellationToken ct)
    {
        var batch = Container.CreateTransactionalBatch(new PartitionKey(StoreId))
            .CreateItem(ProductDocument.From(product, StoreId));
        foreach (var entry in audit) batch.CreateItem(AuditDocument.From(entry, StoreId));

        using var response = await batch.ExecuteAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (_, status) = CosmosQuery.FirstFailure(response);
            if (status == HttpStatusCode.Conflict)
                throw new ConflictException(Domain.Common.ErrorCodes.ValidationFailed, $"Product '{product.Id}' already exists.");
            throw new CosmosException($"Creating product failed: {response.ErrorMessage}", status, 0, response.ActivityId, response.RequestCharge);
        }
        return WithToken(product, response[0].ETag);
    }

    public async Task<Product> SaveAsync(Product product, IReadOnlyList<PriceStockChange> audit, CancellationToken ct)
    {
        var batch = Container.CreateTransactionalBatch(new PartitionKey(StoreId))
            .ReplaceItem(product.Id, ProductDocument.From(product, StoreId),
                new TransactionalBatchItemRequestOptions { IfMatchEtag = product.ConcurrencyToken });
        foreach (var entry in audit) batch.CreateItem(AuditDocument.From(entry, StoreId));

        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode) return WithToken(product, response[0].ETag);

        var (_, status) = CosmosQuery.FirstFailure(response);
        switch (status)
        {
            case HttpStatusCode.PreconditionFailed:
                var latest = await GetAsync(product.Id, ct);
                throw new ConcurrencyConflictException(
                    "The product was changed by someone else since you loaded it. Reload to see the latest values.", latest?.ConcurrencyToken);
            case HttpStatusCode.NotFound:
                throw new NotFoundException("Product", product.Id);
            default:
                throw new CosmosException($"Saving product failed: {response.ErrorMessage}", status, 0, response.ActivityId, response.RequestCharge);
        }
    }

    public async Task<Page<PriceStockChange>> GetAuditAsync(string productId, AuditCursor? after, int pageSize, CancellationToken ct)
    {
        var sql = new StringBuilder($"SELECT TOP {pageSize + 1} * FROM c WHERE c.storeId = @storeId AND c.docType = @docType AND c.productId = @productId");
        var parameters = new Dictionary<string, object>
        {
            ["@storeId"] = StoreId, ["@docType"] = DocTypes.Audit, ["@productId"] = productId
        };
        if (after is not null)
        {
            sql.Append(" AND (c.changedAtUtc < @afterAt OR (c.changedAtUtc = @afterAt AND c.id < @afterId))");
            parameters["@afterAt"] = CosmosJson.Format(after.ChangedAtUtc);
            parameters["@afterId"] = AuditDocument.IdPrefix + after.Id;
        }
        sql.Append(" ORDER BY c.changedAtUtc DESC, c.id DESC");

        var docs = await CosmosQuery.ReadAsync<AuditDocument>(Container, ToQuery(sql.ToString(), parameters), StoreId, pageSize + 1, ct);
        return new Page<PriceStockChange>(docs.Take(pageSize).Select(d => d.ToDomain()).ToList(), docs.Count > pageSize);
    }

    private (StringBuilder Where, Dictionary<string, object> Parameters) BuildFilter(ProductSearchCriteria c)
    {
        var where = new StringBuilder("c.storeId = @storeId AND c.docType = @docType");
        var parameters = new Dictionary<string, object> { ["@storeId"] = StoreId, ["@docType"] = DocTypes.Product };

        if (!c.IncludeInactive) where.Append(" AND c.isActive = true");
        if (c.FeaturedOnly) where.Append(" AND c.isFeatured = true");
        if (c.NameContains is not null)
        {
            // searchName is stored lower-cased; the criteria value is lower-cased by the application layer.
            where.Append(" AND CONTAINS(c.searchName, @name)");
            parameters["@name"] = c.NameContains;
        }
        if (c.Types.Count > 0)
        {
            where.Append(" AND ARRAY_CONTAINS(@types, c.type)");
            parameters["@types"] = c.Types.Select(t => t.ToString()).ToArray();
        }
        if (c.FranchiseCodes.Count > 0)
        {
            where.Append(" AND ARRAY_CONTAINS(@franchises, c.franchiseCode)");
            parameters["@franchises"] = c.FranchiseCodes.ToArray();
        }
        return (where, parameters);
    }

    private static QueryDefinition ToQuery(string sql, Dictionary<string, object> parameters)
    {
        var query = new QueryDefinition(sql);
        foreach (var (name, value) in parameters) query = query.WithParameter(name, value);
        return query;
    }

    private static Product WithToken(Product p, string etag) =>
        Product.Rehydrate(p.Id, p.Name, p.Type, p.FranchiseCode, p.Description, p.Price, p.StockQuantity, p.IsActive,
            p.CreatedAtUtc, p.UpdatedAtUtc, etag, p.IsFeatured, p.Image);
}
