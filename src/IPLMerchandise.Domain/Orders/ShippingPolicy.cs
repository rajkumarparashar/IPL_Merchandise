namespace IPLMerchandise.Domain.Orders;

/// <summary>Flat-rate domestic shipping, free above a threshold. Amounts are tax-inclusive INR.</summary>
public static class ShippingPolicy
{
    public const decimal FlatFee = 99.00m;
    public const decimal FreeShippingThreshold = 999.00m;

    public static decimal FeeFor(decimal subtotal) =>
        subtotal <= 0 || subtotal >= FreeShippingThreshold ? 0m : FlatFee;
}
