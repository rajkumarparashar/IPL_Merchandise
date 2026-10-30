using System.Net;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;

namespace IPLMerchandise.Infrastructure.Repositories;

/// <summary>
/// The stock-deduction transaction. Works only because products, audit entries and commit records share the catalog
/// container's single logical partition (/storeId): Cosmos transactional batches cannot span partitions.
/// </summary>
public sealed class CosmosCheckoutRepository(CosmosContainers cosmos) : ICheckoutRepository
{
    private Container Container => cosmos.Catalog;
    private string StoreId => cosmos.StoreId;

    public async Task<CheckoutCommit?> GetCommitAsync(Guid orderId, CancellationToken ct)
    {
        var doc = await CosmosQuery.ReadOrDefaultAsync<CheckoutCommitDocument>(Container, CheckoutCommitDocument.IdFor(orderId), StoreId, ct);
        return doc?.ToDomain();
    }

    public async Task<CommitResult> CommitAsync(IReadOnlyList<Product> products, IReadOnlyList<PriceStockChange> audit,
        CheckoutCommit commit, CancellationToken ct)
    {
        if (products.Count * 2 + 1 > 100)
            throw new InvalidOperationException("Checkout exceeds the 100-operation transactional batch limit.");

        // Commit record first: if this order ID was already committed, the batch fails on op 0 with 409 and we
        // report AlreadyCommitted, rather than a misleading stock conflict caused by our own earlier deduction.
        var batch = Container.CreateTransactionalBatch(new PartitionKey(StoreId))
            .CreateItem(CheckoutCommitDocument.From(commit, StoreId));

        foreach (var product in products)
        {
            batch.ReplaceItem(product.Id, ProductDocument.From(product, StoreId),
                new TransactionalBatchItemRequestOptions { IfMatchEtag = product.ConcurrencyToken });
        }
        foreach (var entry in audit) batch.CreateItem(AuditDocument.From(entry, StoreId));

        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode) return CommitResult.Committed;

        var (index, status) = CosmosQuery.FirstFailure(response);
        return (index, status) switch
        {
            (0, HttpStatusCode.Conflict) => CommitResult.AlreadyCommitted,
            (_, HttpStatusCode.PreconditionFailed) => CommitResult.ConcurrencyConflict,
            (_, HttpStatusCode.NotFound) => CommitResult.ConcurrencyConflict, // product deleted meanwhile; re-validation reports it
            _ => throw new CosmosException($"Checkout commit failed: {response.ErrorMessage}", status, 0, response.ActivityId, response.RequestCharge)
        };
    }

    public async Task MarkCompletedAsync(Guid orderId, CancellationToken ct)
    {
        try
        {
            await Container.PatchItemAsync<CheckoutCommitDocument>(CheckoutCommitDocument.IdFor(orderId), new PartitionKey(StoreId),
            [
                PatchOperation.Set("/status", nameof(CommitStatus.Completed)),
                PatchOperation.Set("/ttl", CheckoutCommitDocument.CompletedTtlSeconds)
            ], new PatchItemRequestOptions { EnableContentResponseOnWrite = false }, ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already expired: nothing to mark.
        }
    }

    public async Task<IReadOnlyList<CheckoutCommit>> GetPendingAsync(DateTime createdBeforeUtc, int max, CancellationToken ct)
    {
        var query = new QueryDefinition(
                $"SELECT TOP {max} * FROM c WHERE c.storeId = @storeId AND c.docType = @docType AND c.status = @status AND c.createdAtUtc < @before")
            .WithParameter("@storeId", StoreId)
            .WithParameter("@docType", DocTypes.Checkout)
            .WithParameter("@status", nameof(CommitStatus.Pending))
            .WithParameter("@before", CosmosJson.Format(createdBeforeUtc));
        var docs = await CosmosQuery.ReadAsync<CheckoutCommitDocument>(Container, query, StoreId, max, ct);
        return docs.Select(d => d.ToDomain()).ToList();
    }
}
