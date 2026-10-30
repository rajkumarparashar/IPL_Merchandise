using IPLMerchandise.Domain.Catalog;

namespace IPLMerchandise.Domain.Carts;

[Flags]
public enum CartLineIssue
{
    None = 0,
    PriceChanged = 1,
    InsufficientStock = 2,
    OutOfStock = 4,
    Unavailable = 8
}

public sealed record LineReview(CartLine Line, Product? Current, CartLineIssue Issues)
{
    public decimal? CurrentUnitPrice => Current?.Price;
    public int AvailableStock => Current is { IsActive: true } ? Current.StockQuantity : 0;
    public bool CanFulfil => (Issues & (CartLineIssue.InsufficientStock | CartLineIssue.OutOfStock | CartLineIssue.Unavailable)) == 0;

    /// <summary>Quantity the line would have after the customer accepts changes (0 = would be removed).</summary>
    public int QuantityAfterAccept => Current is null || !Current.IsActive
        ? 0
        : Math.Min(Math.Min(Line.Quantity, Current.StockQuantity), Current.MaxQuantityPerLine);

    public static LineReview For(CartLine line, Product? current)
    {
        if (current is null || !current.IsActive)
            return new LineReview(line, current, CartLineIssue.Unavailable);

        var issues = CartLineIssue.None;
        if (current.Price != line.UnitPrice) issues |= CartLineIssue.PriceChanged;
        if (current.StockQuantity == 0) issues |= CartLineIssue.OutOfStock;
        else if (current.StockQuantity < line.Quantity) issues |= CartLineIssue.InsufficientStock;
        return new LineReview(line, current, issues);
    }
}

/// <summary>Result of comparing a cart to the live catalogue. Any issue means the customer must review before checkout.</summary>
public sealed record CartReview(IReadOnlyList<LineReview> Lines)
{
    public bool RequiresReview => Lines.Any(l => l.Issues != CartLineIssue.None);
    public decimal Subtotal => Lines.Sum(l => l.Line.LineTotal);

    /// <summary>What the subtotal becomes if the customer accepts the changes.</summary>
    public decimal SubtotalAfterAccept => Lines.Sum(l => (l.CurrentUnitPrice ?? 0) * l.QuantityAfterAccept);
}
