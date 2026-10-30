namespace IPLMerchandise.Domain.Catalog;

public enum ChangedField
{
    Price,
    Stock
}

public enum ChangeReason
{
    ProductCreated,
    ManualAdjustment,
    Order
}

/// <summary>Who changed what, when and why. Passed into every price/stock mutation so an audit entry can never be forgotten.</summary>
public sealed record ChangeContext(string ChangedBy, ChangeReason Reason, DateTime AtUtc, string? Note = null, string? OrderId = null);

/// <summary>Immutable audit record of one price or stock change.</summary>
public sealed record PriceStockChange(
    string Id,
    string ProductId,
    ChangedField Field,
    decimal OldValue,
    decimal NewValue,
    string ChangedBy,
    DateTime ChangedAtUtc,
    ChangeReason Reason,
    string? Note,
    string? OrderId)
{
    internal static PriceStockChange For(string productId, ChangedField field, decimal oldValue, decimal newValue, ChangeContext ctx) =>
        new(Guid.NewGuid().ToString("N"), productId, field, oldValue, newValue, ctx.ChangedBy, ctx.AtUtc, ctx.Reason, ctx.Note, ctx.OrderId);
}
