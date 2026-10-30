using System.Net;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;

namespace IPLMerchandise.Infrastructure.Repositories;

/// <summary>Cart and its idempotency records share the customer's partition, so both are saved in one transaction.</summary>
public sealed class CosmosCartRepository(CosmosContainers cosmos) : ICartRepository
{
    private Container Container => cosmos.Carts;

    public async Task<Cart> GetAsync(string customerId, CancellationToken ct)
    {
        var doc = await CosmosQuery.ReadOrDefaultAsync<CartDocument>(Container, CartDocument.FixedId, customerId, ct);
        return doc?.ToDomain() ?? Cart.Empty(customerId);
    }

    public async Task<IdempotencyRecord?> GetIdempotencyRecordAsync(string customerId, string key, CancellationToken ct)
    {
        var doc = await CosmosQuery.ReadOrDefaultAsync<IdempotencyDocument>(Container, IdempotencyDocument.IdFor(key), customerId, ct);
        return doc?.ToDomain();
    }

    public async Task<CartSaveResult> SaveAsync(Cart cart, IdempotencyRecord? record, CancellationToken ct)
    {
        var batch = Container.CreateTransactionalBatch(new PartitionKey(cart.CustomerId));

        // Idempotency record first so a duplicate key is detected before anything else.
        if (record is not null) batch.CreateItem(IdempotencyDocument.From(record));

        var doc = CartDocument.From(cart);
        if (cart.ConcurrencyToken is null)
            batch.CreateItem(doc);
        else
            batch.ReplaceItem(doc.Id, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = cart.ConcurrencyToken });

        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode) return CartSaveResult.Saved;

        var (index, status) = CosmosQuery.FirstFailure(response);
        var idempotencyOpFailed = record is not null && index == 0;
        return status switch
        {
            HttpStatusCode.Conflict when idempotencyOpFailed => CartSaveResult.DuplicateRequest,
            HttpStatusCode.Conflict => CartSaveResult.ConcurrencyConflict,           // cart created concurrently
            HttpStatusCode.PreconditionFailed => CartSaveResult.ConcurrencyConflict, // cart changed concurrently
            HttpStatusCode.NotFound => CartSaveResult.ConcurrencyConflict,           // cart removed concurrently
            _ => throw new CosmosException($"Saving cart failed: {response.ErrorMessage}", status, 0, response.ActivityId, response.RequestCharge)
        };
    }
}
