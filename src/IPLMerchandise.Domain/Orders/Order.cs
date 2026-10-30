using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.Domain.Orders;

public enum OrderStatus
{
    /// <summary>Stock is committed and the order is recorded. Payment is out of scope (see README).</summary>
    Confirmed
}

public enum PaymentMethod
{
    CashOnDelivery
}

/// <summary>Delivery address captured at checkout. Personal data: stored only on the order, never logged.</summary>
public sealed record ShippingAddress(string FullName, string Mobile, string Line1, string? Line2, string City, string State, string PinCode);

/// <summary>
/// Snapshot of what was bought. Name, price and photo are copied, so later catalogue edits never change history
/// (replaced photos are kept in storage for this reason).
/// </summary>
public sealed record OrderLine(string ProductId, string ProductName, string FranchiseCode, decimal UnitPrice, int Quantity,
    Catalog.ProductType? Type = null, string? ImageUrl = null)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

public sealed class Order
{
    private Order() { }

    public Guid Id { get; private set; }
    public string CustomerId { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }
    public OrderStatus Status { get; private set; }
    public IReadOnlyList<OrderLine> Lines { get; private set; } = [];
    public decimal Subtotal { get; private set; }
    public decimal ShippingFee { get; private set; }
    public decimal Total { get; private set; }
    public ShippingAddress? ShippingAddress { get; private set; }
    public PaymentMethod PaymentMethod { get; private set; }

    public static Order Place(Guid id, string customerId, IReadOnlyList<OrderLine> lines, DateTime nowUtc,
        ShippingAddress? shippingAddress = null, PaymentMethod paymentMethod = PaymentMethod.CashOnDelivery)
    {
        if (id == Guid.Empty)
            throw new BusinessRuleException(ErrorCodes.ValidationFailed, "Order ID is required.", "orderId");
        if (lines.Count == 0)
            throw new BusinessRuleException(ErrorCodes.CartEmpty, "An order needs at least one line.");

        var subtotal = lines.Sum(l => l.LineTotal);
        var shipping = ShippingPolicy.FeeFor(subtotal);
        return new Order
        {
            Id = id,
            CustomerId = customerId,
            CreatedAtUtc = nowUtc,
            Status = OrderStatus.Confirmed,
            Lines = lines,
            Subtotal = subtotal,
            ShippingFee = shipping,
            Total = subtotal + shipping,
            ShippingAddress = shippingAddress,
            PaymentMethod = paymentMethod
        };
    }

    public static Order Rehydrate(Guid id, string customerId, DateTime createdAtUtc, OrderStatus status,
        IReadOnlyList<OrderLine> lines, decimal subtotal, decimal shippingFee, decimal total,
        ShippingAddress? shippingAddress = null, PaymentMethod paymentMethod = PaymentMethod.CashOnDelivery) =>
        new()
        {
            Id = id, CustomerId = customerId, CreatedAtUtc = createdAtUtc, Status = status,
            Lines = lines, Subtotal = subtotal, ShippingFee = shippingFee, Total = total,
            ShippingAddress = shippingAddress, PaymentMethod = paymentMethod
        };
}
