using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Identity;
using IPLMerchandise.Domain.Orders;

namespace IPLMerchandise.Application.Abstractions;

/// <summary>Empty lists mean "any". Several values within a list are OR-ed; the lists are AND-ed together.</summary>
public sealed record ProductSearchCriteria(
    string? NameContains, IReadOnlyList<ProductType> Types, IReadOnlyList<string> FranchiseCodes, bool IncludeInactive, bool FeaturedOnly = false);

/// <summary>Featured = featured products first, then the rest; each group oldest first (stable).</summary>
public enum ProductSortOrder { Featured, PriceAscending, PriceDescending, Name }

/// <summary>
/// Keyset position in the product list: the last item seen. It carries every sortable key so one cursor type serves
/// every sort order; the repository uses the key that matches the order (always with Id as the tie-breaker).
/// <see cref="Featured"/> tells the "Featured" order which of its two segments (featured first, then the rest) the
/// last item belonged to.
/// </summary>
public sealed record ProductCursor(string Id, DateTime CreatedAtUtc, decimal Price, string SearchName, bool Featured = false);

/// <summary>Keyset position in the order list (newest first: CreatedAtUtc desc, then Id desc).</summary>
public sealed record OrderCursor(DateTime CreatedAtUtc, string Id);

public sealed record AuditCursor(DateTime ChangedAtUtc, string Id);

public sealed record Page<T>(IReadOnlyList<T> Items, bool HasMore);

public interface IProductRepository
{
    Task<Product?> GetAsync(string id, CancellationToken ct);

    /// <summary>Loads several products in one round trip. Missing ids are simply absent from the result.</summary>
    Task<IReadOnlyDictionary<string, Product>> GetManyAsync(IReadOnlyCollection<string> ids, CancellationToken ct);

    Task<Page<Product>> SearchAsync(ProductSearchCriteria criteria, ProductSortOrder sort, ProductCursor? after, int pageSize, CancellationToken ct);

    Task<long> CountAsync(ProductSearchCriteria criteria, CancellationToken ct);

    /// <summary>Type and franchise of every active product whose name contains the text (for facet counts).</summary>
    Task<IReadOnlyList<(ProductType Type, string FranchiseCode)>> GetFacetKeysAsync(string? nameContains, CancellationToken ct);

    /// <summary>Creates the product and its initial audit entries atomically.</summary>
    Task<Product> CreateAsync(Product product, IReadOnlyList<PriceStockChange> audit, CancellationToken ct);

    /// <summary>
    /// Saves the product only if it is unchanged since it was read (its <see cref="Product.ConcurrencyToken"/>),
    /// writing the audit entries in the same transaction. Throws <see cref="Common.ConcurrencyConflictException"/> otherwise.
    /// </summary>
    Task<Product> SaveAsync(Product product, IReadOnlyList<PriceStockChange> audit, CancellationToken ct);

    Task<Page<PriceStockChange>> GetAuditAsync(string productId, AuditCursor? after, int pageSize, CancellationToken ct);
}

/// <summary>Stored outcome of a cart mutation, keyed by the client's Idempotency-Key.</summary>
public sealed record IdempotencyRecord(string Key, string CustomerId, string RequestHash, string ResponseJson, DateTime CreatedAtUtc);

public enum CartSaveResult
{
    Saved,
    /// <summary>The cart changed since it was read (another tab or request). Re-read and re-apply.</summary>
    ConcurrencyConflict,
    /// <summary>The idempotency key was recorded by a concurrent request. Read the record and replay it.</summary>
    DuplicateRequest
}

public interface ICartRepository
{
    /// <summary>Returns the customer's cart, or an empty cart if they have none yet.</summary>
    Task<Cart> GetAsync(string customerId, CancellationToken ct);

    Task<IdempotencyRecord?> GetIdempotencyRecordAsync(string customerId, string key, CancellationToken ct);

    /// <summary>Saves the cart (guarded by its concurrency token) and, if given, the idempotency record, atomically.</summary>
    Task<CartSaveResult> SaveAsync(Cart cart, IdempotencyRecord? record, CancellationToken ct);
}

public enum CommitStatus
{
    /// <summary>Stock is deducted but the order document may not exist yet.</summary>
    Pending,
    Completed
}

/// <summary>
/// Written in the same transaction as the stock deduction. Its id is the order id, so an order can only ever deduct stock once.
/// It carries the full order so the order document can be rebuilt if the process dies before writing it.
/// </summary>
public sealed record CheckoutCommit(Order Order, CommitStatus Status);

public enum CommitResult
{
    Committed,
    /// <summary>A product changed between read and write (another checkout or an admin edit). Re-read and retry.</summary>
    ConcurrencyConflict,
    /// <summary>A commit for this order id already exists.</summary>
    AlreadyCommitted
}

public interface ICheckoutRepository
{
    Task<CheckoutCommit?> GetCommitAsync(Guid orderId, CancellationToken ct);

    /// <summary>
    /// All-or-nothing: saves every product (guarded by its concurrency token), the audit entries and the commit record
    /// in one transaction.
    /// </summary>
    Task<CommitResult> CommitAsync(IReadOnlyList<Product> products, IReadOnlyList<PriceStockChange> audit, CheckoutCommit commit, CancellationToken ct);

    Task MarkCompletedAsync(Guid orderId, CancellationToken ct);

    Task<IReadOnlyList<CheckoutCommit>> GetPendingAsync(DateTime createdBeforeUtc, int max, CancellationToken ct);
}

public sealed record OrderSummary(Guid Id, DateTime CreatedAtUtc, OrderStatus Status, int ItemCount, decimal Total, IReadOnlyList<OrderLine> Lines);

/// <summary>Optional list filters. <see cref="IdPrefix"/> is a lower-case prefix of the order id (what the UI shows as the order number).</summary>
public sealed record OrderListFilter(DateTime? FromUtc, DateTime? ToUtc, string? IdPrefix, OrderStatus? Status);

public interface IOrderRepository
{
    Task<Order?> GetAsync(string customerId, Guid orderId, CancellationToken ct);

    /// <summary>Creates the order; if it already exists, returns the stored one instead (idempotent).</summary>
    Task<Order> CreateIfNotExistsAsync(Order order, CancellationToken ct);

    Task<Page<OrderSummary>> ListAsync(string customerId, OrderListFilter filter, OrderCursor? after, int pageSize, CancellationToken ct);

    /// <summary>(createdAtUtc, total) of every order of the customer, for headline stats.</summary>
    Task<IReadOnlyList<(DateTime CreatedAtUtc, decimal Total)>> GetTotalsAsync(string customerId, CancellationToken ct);
}

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Returns false if a user with the same email already exists.</summary>
    Task<bool> TryCreateAsync(User user, CancellationToken ct);
}

/// <summary>
/// Where product photos are kept (Azure Blob Storage; Azurite locally). Stored files are never overwritten: each upload
/// gets a new random name, so a cached or order-history URL always shows the picture it was created for.
/// </summary>
public interface IProductImageStore
{
    /// <summary>Stores the bytes under a new name and returns the key and public URL. Throws <see cref="Common.DependencyUnavailableException"/> if storage is down.</summary>
    Task<ProductImage> SaveAsync(string productId, ReadOnlyMemory<byte> content, ImageFormat format, CancellationToken ct);

    /// <summary>Deletes a stored file. Used to tidy up an upload whose product save failed; missing files are ignored.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

/// <summary>
/// Tells connected shoppers that products changed (price, stock, availability), so open carts re-check themselves at once
/// instead of polling. Only ids are sent: each client re-reads its own cart, and the server decides what to flag.
/// Best effort: it must never fail or slow down the write that triggered it; checkout re-validates anyway.
/// </summary>
public interface IProductChangeNotifier
{
    Task ProductsChangedAsync(IReadOnlyCollection<string> productIds);
}

/// <summary>Used when no real-time channel is configured (e.g. background tools): nobody is told, nothing breaks.</summary>
public sealed class NoProductChangeNotifier : IProductChangeNotifier
{
    public Task ProductsChangedAsync(IReadOnlyCollection<string> productIds) => Task.CompletedTask;
}
