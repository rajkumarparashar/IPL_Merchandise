using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Application.Carts;

/// <summary>Result of a cart mutation. <see cref="Replayed"/> is true when the Idempotency-Key was seen before.</summary>
public sealed record CartMutationResult(CartDto Cart, bool Replayed);

/// <summary>
/// Cart use cases. Every mutation:
///  1. replays the stored result if the Idempotency-Key was already used (same request) or rejects it (different request);
///  2. otherwise applies the change to the latest cart and saves cart + idempotency record in one transaction;
///  3. if the cart changed underneath (two tabs), re-reads and re-applies, a bounded number of times.
/// Rejected requests (e.g. out of stock) change nothing and are not recorded, so retrying them simply re-evaluates.
/// </summary>
public sealed partial class CartService(
    ICartRepository carts,
    IProductRepository products,
    TimeProvider clock,
    ILogger<CartService> logger)
{
    internal const int MaxAttempts = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CartDto> GetAsync(string customerId, CancellationToken ct)
    {
        var cart = await carts.GetAsync(customerId, ct);
        var current = await products.GetManyAsync(cart.Lines.Select(l => l.ProductId).ToList(), ct);
        return cart.Review(current).ToDto();
    }

    public Task<CartMutationResult> AddItemAsync(string customerId, string? idempotencyKey, AddCartItemRequest request, CancellationToken ct) =>
        MutateAsync(customerId, idempotencyKey, $"add|{request.ProductId}|{request.Quantity}", request.ProductId,
            (cart, product, now) => cart.AddItem(product!, request.Quantity!.Value, now), ct);

    public Task<CartMutationResult> UpdateQuantityAsync(string customerId, string? idempotencyKey, string productId, UpdateCartItemRequest request, CancellationToken ct) =>
        MutateAsync(customerId, idempotencyKey, $"set|{productId}|{request.Quantity}", productId,
            (cart, product, now) => cart.SetQuantity(product!, request.Quantity!.Value, now), ct);

    public Task<CartMutationResult> RemoveItemAsync(string customerId, string? idempotencyKey, string productId, CancellationToken ct) =>
        MutateAsync(customerId, idempotencyKey, $"remove|{productId}", null,
            (cart, _, now) => cart.Remove(productId, now), ct);

    public Task<CartMutationResult> ClearAsync(string customerId, string? idempotencyKey, CancellationToken ct) =>
        MutateAsync(customerId, idempotencyKey, "clear", null, (cart, _, now) => cart.Clear(now), ct);

    /// <summary>The customer accepts new prices / reduced quantities after a catalogue change.</summary>
    public Task<CartMutationResult> AcceptChangesAsync(string customerId, string? idempotencyKey, CancellationToken ct) =>
        MutateAsync(customerId, idempotencyKey, "accept", null, null, ct);

    private async Task<CartMutationResult> MutateAsync(
        string customerId, string? idempotencyKey, string requestDescriptor, string? targetProductId,
        Action<Cart, Product?, DateTime>? mutate, CancellationToken ct)
    {
        var key = ValidateKey(idempotencyKey);
        var requestHash = Hash(requestDescriptor);

        var existing = await carts.GetIdempotencyRecordAsync(customerId, key, ct);
        if (existing is not null) return Replay(existing, requestHash);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var cart = await carts.GetAsync(customerId, ct);
            var ids = cart.Lines.Select(l => l.ProductId).ToHashSet();
            if (targetProductId is not null) ids.Add(targetProductId);
            var current = await products.GetManyAsync(ids, ct);

            var now = clock.GetUtcNow().UtcDateTime;
            if (mutate is null)
            {
                cart.AcceptChanges(current, now);
            }
            else
            {
                Product? target = null;
                if (targetProductId is not null && !current.TryGetValue(targetProductId, out target))
                    throw new NotFoundException("Product", targetProductId);
                mutate(cart, target, now);
            }

            var dto = cart.Review(current).ToDto();
            var record = new IdempotencyRecord(key, customerId, requestHash, JsonSerializer.Serialize(dto, Json), now);

            switch (await carts.SaveAsync(cart, record, ct))
            {
                case CartSaveResult.Saved:
                    return new CartMutationResult(dto, Replayed: false);

                case CartSaveResult.DuplicateRequest:
                    // A concurrent request with the same key won the race (double-click). Return its result.
                    var winner = await carts.GetIdempotencyRecordAsync(customerId, key, ct)
                                 ?? throw RetryLater();
                    return Replay(winner, requestHash);

                case CartSaveResult.ConcurrencyConflict:
                    StoreTelemetry.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("operation", "cart"));
                    logger.LogDebug("Cart for {CustomerId} changed concurrently; attempt {Attempt}", customerId, attempt);
                    continue;
            }
        }

        throw new ConflictException(ErrorCodes.ConcurrencyConflict, "Your cart is being changed from somewhere else. Please try again.");
    }

    private CartMutationResult Replay(IdempotencyRecord record, string requestHash)
    {
        if (record.RequestHash != requestHash)
            throw new ConflictException(ErrorCodes.IdempotencyKeyReused,
                "This Idempotency-Key was already used for a different cart change. Generate a new key for each new action.");

        StoreTelemetry.IdempotentReplays.Add(1, new KeyValuePair<string, object?>("operation", "cart"));
        logger.LogInformation("Replayed cart request {IdempotencyKey}", record.Key);
        return new CartMutationResult(JsonSerializer.Deserialize<CartDto>(record.ResponseJson, Json)!, Replayed: true);
    }

    /// <summary>The winning record is not visible yet (replica lag across instances). Nothing was written; retrying is safe.</summary>
    private static ConflictException RetryLater() =>
        new(ErrorCodes.ConcurrencyConflict, "The same request is being processed. Please retry.");

    internal static string ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw RequestValidationException.For(ApiHeaders.IdempotencyKey, $"The {ApiHeaders.IdempotencyKey} header is required for cart changes.");
        if (!KeyPattern().IsMatch(key))
            throw RequestValidationException.For(ApiHeaders.IdempotencyKey,
                $"The {ApiHeaders.IdempotencyKey} must be 8–64 characters of letters, digits, '-' or '_' (a GUID is ideal).");
        return key;
    }

    private static string Hash(string descriptor) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex KeyPattern();
}
