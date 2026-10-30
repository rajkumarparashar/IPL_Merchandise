using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Domain.Catalog;

public sealed class Product
{
    private Product() { }

    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public ProductType Type { get; private set; }
    public string FranchiseCode { get; private set; } = default!;
    public string Description { get; private set; } = default!;
    public decimal Price { get; private set; }
    public int StockQuantity { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Promoted on the home page. A merchandising choice made by admins.</summary>
    public bool IsFeatured { get; private set; }
    /// <summary>Optional photo. Null means the store shows its drawn illustration for the product type.</summary>
    public ProductImage? Image { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Opaque optimistic-concurrency token (the Cosmos ETag). Null until persisted.</summary>
    public string? ConcurrencyToken { get; private set; }

    /// <summary>Signed photographs are limited editions: tighter per-line cap (anti-scalping).</summary>
    public bool IsLimitedEdition => Type == ProductType.SignedPhotograph;
    public StockStatus StockStatus => CatalogRules.StatusFor(StockQuantity);
    public int MaxQuantityPerLine => IsLimitedEdition ? CartRules.MaxLimitedEditionPerLine : CartRules.MaxQuantityPerLine;

    public static (Product Product, IReadOnlyList<PriceStockChange> Audit) Create(
        string id, string name, ProductType type, string franchiseCode, string description,
        decimal price, int stockQuantity, ChangeContext ctx, bool isFeatured = false)
    {
        var product = new Product { Id = id, CreatedAtUtc = ctx.AtUtc, IsActive = true, IsFeatured = isFeatured };
        product.ApplyDetails(name, type, franchiseCode, description, ctx.AtUtc);
        CatalogRules.EnsureValidPrice(price);
        CatalogRules.EnsureValidStock(stockQuantity);
        product.Price = price;
        product.StockQuantity = stockQuantity;

        var created = ctx with { Reason = ChangeReason.ProductCreated };
        return (product,
        [
            PriceStockChange.For(id, ChangedField.Price, 0, price, created),
            PriceStockChange.For(id, ChangedField.Stock, 0, stockQuantity, created)
        ]);
    }

    /// <summary>Rebuilds a product from storage. Skips validation: stored data was validated when written.</summary>
    public static Product Rehydrate(string id, string name, ProductType type, string franchiseCode, string description,
        decimal price, int stockQuantity, bool isActive, DateTime createdAtUtc, DateTime updatedAtUtc, string? concurrencyToken,
        bool isFeatured = false, ProductImage? image = null) =>
        new()
        {
            Id = id, Name = name, Type = type, FranchiseCode = franchiseCode, Description = description,
            Price = price, StockQuantity = stockQuantity, IsActive = isActive, IsFeatured = isFeatured, Image = image,
            CreatedAtUtc = createdAtUtc, UpdatedAtUtc = updatedAtUtc, ConcurrencyToken = concurrencyToken
        };

    /// <param name="isFeatured">Null leaves the flag unchanged (older clients do not send it).</param>
    public void UpdateDetails(string name, ProductType type, string franchiseCode, string description, bool isActive, DateTime nowUtc,
        bool? isFeatured = null)
    {
        ApplyDetails(name, type, franchiseCode, description, nowUtc);
        IsActive = isActive;
        if (isFeatured is { } featured) IsFeatured = featured;
    }

    /// <summary>Sets or (with null) removes the photo. Returns the previous one.</summary>
    public ProductImage? SetImage(ProductImage? image, DateTime nowUtc)
    {
        var previous = Image;
        Image = image;
        UpdatedAtUtc = nowUtc;
        return previous;
    }

    /// <summary>Returns the audit entry, or null when the price is unchanged (no write needed).</summary>
    public PriceStockChange? ChangePrice(decimal newPrice, ChangeContext ctx)
    {
        CatalogRules.EnsureValidPrice(newPrice);
        if (newPrice == Price) return null;
        var change = PriceStockChange.For(Id, ChangedField.Price, Price, newPrice, ctx);
        Price = newPrice;
        UpdatedAtUtc = ctx.AtUtc;
        return change;
    }

    /// <summary>Manual stock set by an admin. Returns null when unchanged.</summary>
    public PriceStockChange? SetStock(int newQuantity, ChangeContext ctx)
    {
        CatalogRules.EnsureValidStock(newQuantity);
        if (newQuantity == StockQuantity) return null;
        var change = PriceStockChange.For(Id, ChangedField.Stock, StockQuantity, newQuantity, ctx);
        StockQuantity = newQuantity;
        UpdatedAtUtc = ctx.AtUtc;
        return change;
    }

    /// <summary>Removes sold units. Never lets stock go below zero.</summary>
    public PriceStockChange DeductStock(int quantity, ChangeContext ctx)
    {
        if (quantity <= 0)
            throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Quantity to deduct must be positive.", "quantity");
        if (!IsActive)
            throw new ProductUnavailableException(Id, Name);
        if (quantity > StockQuantity)
            throw new InsufficientStockException(Id, Name, quantity, StockQuantity);

        var change = PriceStockChange.For(Id, ChangedField.Stock, StockQuantity, StockQuantity - quantity, ctx);
        StockQuantity -= quantity;
        UpdatedAtUtc = ctx.AtUtc;
        return change;
    }

    private void ApplyDetails(string name, ProductType type, string franchiseCode, string description, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > CatalogRules.MaxNameLength)
            throw new BusinessRuleException(ErrorCodes.InvalidProduct, $"Name is required and must be at most {CatalogRules.MaxNameLength} characters.", "name");
        if (!Enum.IsDefined(type))
            throw new BusinessRuleException(ErrorCodes.InvalidProduct, "Unknown product type.", "type");
        if (!Franchises.TryGet(franchiseCode, out var franchise))
            throw new BusinessRuleException(ErrorCodes.InvalidProduct, $"Unknown franchise '{franchiseCode}'.", "franchiseCode");
        if ((description ?? string.Empty).Length > CatalogRules.MaxDescriptionLength)
            throw new BusinessRuleException(ErrorCodes.InvalidProduct, $"Description must be at most {CatalogRules.MaxDescriptionLength} characters.", "description");

        Name = name.Trim();
        Type = type;
        FranchiseCode = franchise.Code;
        Description = description?.Trim() ?? string.Empty;
        UpdatedAtUtc = nowUtc;
    }
}
