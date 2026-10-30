using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;
using IPLMerchandise.Domain.Orders;

namespace IPLMerchandise.Domain.Carts;

public sealed class CartLine
{
    public required string ProductId { get; init; }
    public required string ProductName { get; set; }
    public required string FranchiseCode { get; set; }

    /// <summary>The price the customer has seen and accepted. Only <see cref="Cart.AcceptChanges"/> moves it.</summary>
    public required decimal UnitPrice { get; set; }
    public required int Quantity { get; set; }
    public required DateTime AddedAtUtc { get; init; }

    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>One customer's cart. Lines keep the price the customer accepted so that later price changes are detected, never silently applied.</summary>
public sealed class Cart
{
    private readonly List<CartLine> _lines;

    private const int RememberedOrders = 10;
    private readonly List<Guid> _appliedOrderIds;

    private Cart(string customerId, List<CartLine> lines, List<Guid> appliedOrderIds, DateTime updatedAtUtc, string? token)
    {
        CustomerId = customerId;
        _lines = lines;
        _appliedOrderIds = appliedOrderIds;
        UpdatedAtUtc = updatedAtUtc;
        ConcurrencyToken = token;
    }

    public string CustomerId { get; }
    public IReadOnlyList<CartLine> Lines => _lines;

    /// <summary>Recent orders already taken out of this cart, so post-checkout cleanup can safely run more than once.</summary>
    public IReadOnlyList<Guid> AppliedOrderIds => _appliedOrderIds;
    public DateTime UpdatedAtUtc { get; private set; }
    public string? ConcurrencyToken { get; }
    public bool IsEmpty => _lines.Count == 0;
    public decimal Subtotal => _lines.Sum(l => l.LineTotal);

    public static Cart Empty(string customerId) => new(customerId, [], [], DateTime.MinValue, null);

    public static Cart Rehydrate(string customerId, IEnumerable<CartLine> lines, IEnumerable<Guid> appliedOrderIds, DateTime updatedAtUtc, string? token) =>
        new(customerId, lines.ToList(), appliedOrderIds.ToList(), updatedAtUtc, token);

    public CartLine? Find(string productId) => _lines.FirstOrDefault(l => l.ProductId == productId);

    /// <summary>Adds units. If the product is already in the cart, the quantities are combined.</summary>
    public void AddItem(Product product, int quantity, DateTime nowUtc)
    {
        if (quantity < CartRules.MinQuantityPerLine)
            throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Quantity must be at least 1.", "quantity");

        var existing = Find(product.Id);
        if (existing is null && _lines.Count >= CartRules.MaxLines)
            throw new BusinessRuleException(ErrorCodes.CartLineLimit, $"A cart can hold at most {CartRules.MaxLines} different products.");

        var newQuantity = (existing?.Quantity ?? 0) + quantity;
        EnsureQuantityAllowed(product, newQuantity);

        if (existing is null)
        {
            _lines.Add(new CartLine
            {
                ProductId = product.Id,
                ProductName = product.Name,
                FranchiseCode = product.FranchiseCode,
                UnitPrice = product.Price,
                Quantity = newQuantity,
                AddedAtUtc = nowUtc
            });
        }
        else
        {
            existing.Quantity = newQuantity;
        }
        UpdatedAtUtc = nowUtc;
    }

    public void SetQuantity(Product product, int quantity, DateTime nowUtc)
    {
        var line = Find(product.Id) ?? throw new CartLineNotFoundException(product.Id);
        EnsureQuantityAllowed(product, quantity);
        line.Quantity = quantity;
        UpdatedAtUtc = nowUtc;
    }

    public void Remove(string productId, DateTime nowUtc)
    {
        var line = Find(productId) ?? throw new CartLineNotFoundException(productId);
        _lines.Remove(line);
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Removes every line. Emptying an empty cart is allowed (idempotent).</summary>
    public void Clear(DateTime nowUtc)
    {
        _lines.Clear();
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Compares every line against the live catalogue. Pure: does not change the cart.</summary>
    public CartReview Review(IReadOnlyDictionary<string, Product> current) =>
        new(_lines.Select(l => LineReview.For(l, current.GetValueOrDefault(l.ProductId))).ToList());

    /// <summary>
    /// The customer accepts the current state of the catalogue: prices move to the live price, quantities are reduced to
    /// what is in stock, and lines that can no longer be bought are dropped. Returns true if anything changed.
    /// </summary>
    public bool AcceptChanges(IReadOnlyDictionary<string, Product> current, DateTime nowUtc)
    {
        var changed = false;
        foreach (var line in _lines.ToList())
        {
            var product = current.GetValueOrDefault(line.ProductId);
            if (product is null || !product.IsActive || product.StockQuantity == 0)
            {
                _lines.Remove(line);
                changed = true;
                continue;
            }

            var allowed = Math.Min(Math.Min(line.Quantity, product.StockQuantity), product.MaxQuantityPerLine);
            if (allowed != line.Quantity) { line.Quantity = allowed; changed = true; }
            if (line.UnitPrice != product.Price) { line.UnitPrice = product.Price; changed = true; }
            if (line.ProductName != product.Name) { line.ProductName = product.Name; changed = true; }
        }
        if (changed) UpdatedAtUtc = nowUtc;
        return changed;
    }

    /// <summary>
    /// After a successful order, take the bought units out of the cart (anything added meanwhile stays).
    /// Idempotent: returns false and does nothing if this order was already applied.
    /// </summary>
    public bool RemovePurchased(Order order, DateTime nowUtc)
    {
        if (_appliedOrderIds.Contains(order.Id)) return false;
        foreach (var bought in order.Lines)
        {
            var line = Find(bought.ProductId);
            if (line is null) continue;
            line.Quantity -= bought.Quantity;
            if (line.Quantity <= 0) _lines.Remove(line);
        }
        _appliedOrderIds.Add(order.Id);
        if (_appliedOrderIds.Count > RememberedOrders) _appliedOrderIds.RemoveAt(0);
        UpdatedAtUtc = nowUtc;
        return true;
    }

    private static void EnsureQuantityAllowed(Product product, int quantity)
    {
        if (!product.IsActive)
            throw new ProductUnavailableException(product.Id, product.Name);
        if (quantity < CartRules.MinQuantityPerLine)
            throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Quantity must be at least 1.", "quantity");
        if (quantity > product.MaxQuantityPerLine)
            throw new BusinessRuleException(ErrorCodes.InvalidQuantity,
                product.IsLimitedEdition
                    ? $"Limited editions are limited to {product.MaxQuantityPerLine} per customer order."
                    : $"Quantity cannot be more than {product.MaxQuantityPerLine} per line.",
                "quantity");
        if (quantity > product.StockQuantity)
            throw new InsufficientStockException(product.Id, product.Name, quantity, product.StockQuantity);
    }
}

public sealed class CartLineNotFoundException(string productId)
    : DomainException(ErrorCodes.NotFound, $"Product '{productId}' is not in the cart.")
{
    public string ProductId { get; } = productId;
}
