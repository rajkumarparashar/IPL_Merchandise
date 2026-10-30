using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Domain.Catalog;

public static class CatalogRules
{
    public const decimal MaxPrice = 1_000_000m;     // ₹10,00,000 – guards against fat-finger admin edits.
    public const int MaxStock = 100_000;
    public const int LowStockThreshold = 5;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 2000;

    /// <summary>Prices are tax-inclusive INR with at most two decimal places (paise).</summary>
    public static void EnsureValidPrice(decimal price)
    {
        if (price <= 0)
            throw new BusinessRuleException(ErrorCodes.InvalidPrice, "Price must be greater than zero.", "price");
        if (price > MaxPrice)
            throw new BusinessRuleException(ErrorCodes.InvalidPrice, $"Price cannot exceed {MaxPrice:0.00}.", "price");
        if (decimal.Round(price, 2) != price)
            throw new BusinessRuleException(ErrorCodes.InvalidPrice, "Price can have at most two decimal places.", "price");
    }

    public static void EnsureValidStock(int stock)
    {
        if (stock < 0)
            throw new BusinessRuleException(ErrorCodes.InvalidStock, "Stock cannot be negative.", "stockQuantity");
        if (stock > MaxStock)
            throw new BusinessRuleException(ErrorCodes.InvalidStock, $"Stock cannot exceed {MaxStock}.", "stockQuantity");
    }

    public static StockStatus StatusFor(int stock) => stock switch
    {
        <= 0 => StockStatus.OutOfStock,
        <= LowStockThreshold => StockStatus.LowStock,
        _ => StockStatus.InStock
    };
}
