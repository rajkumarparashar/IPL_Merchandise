using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Catalog;
using Microsoft.Extensions.Logging;
using DomainProductType = IPLMerchandise.Domain.Catalog.ProductType;

namespace IPLMerchandise.Application.Catalog;

/// <summary>
/// Admin write side of the catalogue. Every change is conditional on the version the admin last saw (If-Match),
/// so two admins can never overwrite each other's edits, and every price/stock change writes an audit entry atomically.
/// </summary>
public sealed class ProductAdminService(
    IProductRepository products, IProductImageStore images, IProductChangeNotifier changes, TimeProvider clock,
    ILogger<ProductAdminService> logger)
{
    public async Task<ProductDto> CreateAsync(CreateProductRequest request, Actor actor, CancellationToken ct)
    {
        var ctx = new ChangeContext(actor.AuditName, ChangeReason.ProductCreated, clock.GetUtcNow().UtcDateTime, "Product created");
        var (product, audit) = Product.Create(
            Guid.NewGuid().ToString("N"), request.Name, Mapping.MapEnum<DomainProductType>(request.Type!.Value),
            request.FranchiseCode, request.Description, request.Price!.Value, request.StockQuantity!.Value, ctx, request.IsFeatured);

        var saved = await products.CreateAsync(product, audit, ct);
        logger.LogInformation("Product {ProductId} created by {UserId}", saved.Id, actor.UserId);
        return saved.ToDto();
    }

    public Task<ProductDto> UpdateDetailsAsync(string id, UpdateProductRequest request, string? ifMatch, Actor actor, CancellationToken ct) =>
        MutateAsync(id, ifMatch, actor, "details", product =>
        {
            product.UpdateDetails(request.Name, Mapping.MapEnum<DomainProductType>(request.Type!.Value), request.FranchiseCode,
                request.Description, request.IsActive, clock.GetUtcNow().UtcDateTime, request.IsFeatured);
            return [];
        }, ct);

    public Task<ProductDto> ChangePriceAsync(string id, ChangePriceRequest request, string? ifMatch, Actor actor, CancellationToken ct) =>
        MutateAsync(id, ifMatch, actor, "price", product =>
        {
            var change = product.ChangePrice(request.Price!.Value, Manual(actor, request.Reason));
            return change is null ? [] : [change];
        }, ct);

    public Task<ProductDto> SetStockAsync(string id, SetStockRequest request, string? ifMatch, Actor actor, CancellationToken ct) =>
        MutateAsync(id, ifMatch, actor, "stock", product =>
        {
            var change = product.SetStock(request.StockQuantity!.Value, Manual(actor, request.Reason));
            return change is null ? [] : [change];
        }, ct);

    /// <summary>
    /// Sets or replaces the product photo. The file is checked first (format from its bytes, size), then stored under a new
    /// name, then the product is saved conditionally. The previous photo is kept: past orders still show it.
    /// </summary>
    public async Task<ProductDto> SetImageAsync(string id, ReadOnlyMemory<byte> content, string? ifMatch, Actor actor, CancellationToken ct)
    {
        var product = await LoadForUpdateAsync(id, ifMatch, "image", ct);
        var format = ProductImageRules.EnsureValid(content.Span);

        var image = await images.SaveAsync(product.Id, content, format, ct);
        product.SetImage(image, clock.GetUtcNow().UtcDateTime);
        try
        {
            var saved = await products.SaveAsync(product, [], ct);
            logger.LogInformation("Product {ProductId} image set to {ImageKey} by {UserId}", id, image.Key, actor.UserId);
            await changes.ProductsChangedAsync([id]);
            return saved.ToDto();
        }
        catch (ConcurrencyConflictException)
        {
            // Definitely not saved (someone else changed the product meanwhile), so the new file is unused: remove it.
            // Other failures (e.g. a timeout) may have saved, so the file is left in place rather than risk a broken image.
            await TryDeleteAsync(image.Key);
            throw;
        }
    }

    /// <summary>Removes the photo; the store falls back to the illustration. The stored file is kept for order history.</summary>
    public Task<ProductDto> RemoveImageAsync(string id, string? ifMatch, Actor actor, CancellationToken ct) =>
        MutateAsync(id, ifMatch, actor, "image", product =>
        {
            product.SetImage(null, clock.GetUtcNow().UtcDateTime);
            return [];
        }, ct);

    public async Task<PagedResult<AuditEntryDto>> GetAuditAsync(string productId, int? pageSize, string? token, CancellationToken ct)
    {
        var size = Paging.NormalisePageSize(pageSize);
        _ = await products.GetAsync(productId, ct) ?? throw new NotFoundException("Product", productId);

        var fingerprint = ContinuationToken.Fingerprint("audit", productId);
        var after = ContinuationToken.Decode<AuditCursor>(token, fingerprint);
        var page = await products.GetAuditAsync(productId, after, size, ct);
        var last = page.Items.LastOrDefault();
        var next = page.HasMore && last is not null ? ContinuationToken.Encode(new AuditCursor(last.ChangedAtUtc, last.Id), fingerprint) : null;
        return new PagedResult<AuditEntryDto>(page.Items.Select(a => a.ToDto()).ToList(), size, next);
    }

    private ChangeContext Manual(Actor actor, string reason) =>
        new(actor.AuditName, ChangeReason.ManualAdjustment, clock.GetUtcNow().UtcDateTime, reason.Trim());

    private async Task<ProductDto> MutateAsync(string id, string? ifMatch, Actor actor, string operation,
        Func<Product, IReadOnlyList<PriceStockChange>> mutate, CancellationToken ct)
    {
        var product = await LoadForUpdateAsync(id, ifMatch, operation, ct);
        var audit = mutate(product);
        var saved = await products.SaveAsync(product, audit, ct);
        logger.LogInformation("Product {ProductId} {Operation} updated by {UserId} ({AuditCount} audit entries)",
            id, operation, actor.UserId, audit.Count);
        await changes.ProductsChangedAsync([id]); // open carts holding it re-check now
        return saved.ToDto();
    }

    /// <summary>Loads the product for a change, requiring If-Match and failing fast when it is already stale.</summary>
    private async Task<Product> LoadForUpdateAsync(string id, string? ifMatch, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw new PreconditionRequiredException("This update must include an If-Match header with the product's ETag.");

        var product = await products.GetAsync(id, ct) ?? throw new NotFoundException("Product", id);

        // Fail fast if the admin's copy is already stale. The repository re-checks atomically on write.
        if (product.ConcurrencyToken != ifMatch)
        {
            StoreTelemetry.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("operation", $"product.{operation}"));
            throw new ConcurrencyConflictException(
                "The product was changed by someone else since you loaded it. Reload to see the latest values.", product.ConcurrencyToken);
        }
        return product;
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await images.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete unused product image {ImageKey}", key);
        }
    }
}
