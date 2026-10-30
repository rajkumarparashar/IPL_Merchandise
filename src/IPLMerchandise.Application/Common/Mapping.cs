using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Orders;
using V1 = IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Application.Common;

/// <summary>Domain → v1 wire contract. Enums are mapped by name so a reorder in either enum cannot silently corrupt data.</summary>
public static class Mapping
{
    public static TTo MapEnum<TTo>(Enum value) where TTo : struct, Enum => Enum.Parse<TTo>(value.ToString());

    public static V1.ProductDto ToDto(this Product p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Type = MapEnum<V1.ProductType>(p.Type),
        FranchiseCode = p.FranchiseCode,
        FranchiseName = Franchises.NameOf(p.FranchiseCode),
        Description = p.Description,
        Price = p.Price,
        StockQuantity = p.StockQuantity,
        StockStatus = MapEnum<V1.StockStatus>(p.StockStatus),
        IsLimitedEdition = p.IsLimitedEdition,
        IsActive = p.IsActive,
        UpdatedAtUtc = p.UpdatedAtUtc,
        ETag = p.ConcurrencyToken ?? "",
        MaxQuantityPerLine = p.MaxQuantityPerLine,
        IsFeatured = p.IsFeatured,
        ImageUrl = p.Image?.Url
    };

    public static V1.AuditEntryDto ToDto(this PriceStockChange a) => new(
        a.Id, a.ProductId, MapEnum<V1.AuditField>(a.Field), a.OldValue, a.NewValue, a.ChangedBy, a.ChangedAtUtc,
        MapEnum<V1.AuditReason>(a.Reason), a.Note, a.OrderId);

    public static V1.OrderDto ToDto(this Order o) => new(
        o.Id, o.CreatedAtUtc, MapEnum<V1.OrderStatus>(o.Status),
        o.Lines.Select(l => new V1.OrderLineDto(l.ProductId, l.ProductName, l.FranchiseCode, l.UnitPrice, l.Quantity, l.LineTotal)
            { Type = l.Type is null ? null : MapEnum<V1.ProductType>(l.Type.Value), FranchiseName = Franchises.NameOf(l.FranchiseCode), ImageUrl = l.ImageUrl }).ToList(),
        o.Subtotal, o.ShippingFee, o.Total)
    {
        ShippingAddress = o.ShippingAddress is not { } a ? null : new V1.ShippingAddressDto
        {
            FullName = a.FullName, Mobile = a.Mobile, Line1 = a.Line1, Line2 = a.Line2, City = a.City, State = a.State, PinCode = a.PinCode
        },
        PaymentMethod = MapEnum<V1.PaymentMethod>(o.PaymentMethod)
    };

    public static V1.OrderSummaryDto ToDto(this OrderSummary s) =>
        new(s.Id, s.CreatedAtUtc, MapEnum<V1.OrderStatus>(s.Status), s.ItemCount, s.Total)
        {
            Items = s.Lines.Select(l => new V1.OrderItemPreviewDto(l.ProductName, l.FranchiseCode,
                l.Type is null ? null : MapEnum<V1.ProductType>(l.Type.Value), l.Quantity) { ImageUrl = l.ImageUrl }).ToList()
        };

    public static V1.CartDto ToDto(this CartReview review)
    {
        var subtotal = review.Subtotal;
        var afterAccept = review.SubtotalAfterAccept;
        return new V1.CartDto
        {
            Lines = review.Lines.Select(ToDto).ToList(),
            ItemCount = review.Lines.Sum(l => l.Line.Quantity),
            Subtotal = subtotal,
            ShippingFee = ShippingPolicy.FeeFor(subtotal),
            Total = subtotal + ShippingPolicy.FeeFor(subtotal),
            RequiresReview = review.RequiresReview,
            TotalAfterAccept = afterAccept + ShippingPolicy.FeeFor(afterAccept)
        };
    }

    private static V1.CartLineDto ToDto(LineReview r) => new()
    {
        ProductId = r.Line.ProductId,
        ProductName = r.Line.ProductName,
        FranchiseCode = r.Line.FranchiseCode,
        FranchiseName = Franchises.NameOf(r.Line.FranchiseCode),
        Type = r.Current is null ? null : MapEnum<V1.ProductType>(r.Current.Type),
        ImageUrl = r.Current?.Image?.Url,
        UnitPrice = r.Line.UnitPrice,
        CurrentUnitPrice = r.CurrentUnitPrice,
        Quantity = r.Line.Quantity,
        AvailableStock = r.AvailableStock,
        MaxQuantity = r.Current is null ? CartRules.MaxQuantityPerLine : Math.Min(r.Current.MaxQuantityPerLine, Math.Max(r.AvailableStock, 1)),
        LineTotal = r.Line.LineTotal,
        Issues = Enum.GetValues<CartLineIssue>()
            .Where(i => i != CartLineIssue.None && r.Issues.HasFlag(i))
            .Select(i => MapEnum<V1.CartLineIssue>(i))
            .ToList()
    };
}
