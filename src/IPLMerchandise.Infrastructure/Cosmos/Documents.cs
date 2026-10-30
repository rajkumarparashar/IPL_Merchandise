using System.Text.Json.Serialization;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Identity;
using IPLMerchandise.Domain.Orders;

namespace IPLMerchandise.Infrastructure.Cosmos;

// Persistence shapes. Kept separate from domain entities so storage details (_etag, ttl, docType, id prefixes)
// never leak into the domain, and so the stored schema can evolve independently.

public static class DocTypes
{
    public const string Product = "product";
    public const string Audit = "audit";
    public const string Checkout = "checkout";
    public const string Cart = "cart";
    public const string Idempotency = "idempotency";
    public const string Order = "order";
    public const string User = "user";
}

public abstract class CosmosDocument
{
    public string Id { get; set; } = "";
    public string DocType { get; set; } = "";

    [JsonPropertyName("_etag")]
    public string? ETag { get; set; }

    /// <summary>Per-document time-to-live in seconds; omitted = never expires.</summary>
    [JsonPropertyName("ttl")]
    public int? Ttl { get; set; }
}

public sealed class ProductDocument : CosmosDocument
{
    public string StoreId { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Lower-cased name for case-insensitive CONTAINS without per-query LOWER() cost.</summary>
    public string SearchName { get; set; } = "";
    public ProductType Type { get; set; }
    public string FranchiseCode { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public int StockQuantity { get; set; }
    public bool IsActive { get; set; }
    public bool IsFeatured { get; set; }

    /// <summary>Absent for products without a photo (null fields are not written).</summary>
    public ProductImageDocument? Image { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public static ProductDocument From(Product p, string storeId) => new()
    {
        Image = p.Image is not { } i ? null : new ProductImageDocument { Key = i.Key, Url = i.Url, ContentType = i.ContentType },
        Id = p.Id, DocType = DocTypes.Product, StoreId = storeId, Name = p.Name, SearchName = p.Name.ToLowerInvariant(),
        Type = p.Type, FranchiseCode = p.FranchiseCode, Description = p.Description, Price = p.Price,
        StockQuantity = p.StockQuantity, IsActive = p.IsActive, IsFeatured = p.IsFeatured, CreatedAtUtc = p.CreatedAtUtc, UpdatedAtUtc = p.UpdatedAtUtc
    };

    public Product ToDomain() => Product.Rehydrate(Id, Name, Type, FranchiseCode, Description, Price, StockQuantity, IsActive,
        CreatedAtUtc, UpdatedAtUtc, ETag, IsFeatured, Image is not { } i ? null : new ProductImage(i.Key, i.Url, i.ContentType));
}

public sealed class ProductImageDocument
{
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public string ContentType { get; set; } = "";
}

public sealed class AuditDocument : CosmosDocument
{
    public const string IdPrefix = "audit:";

    public string StoreId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public ChangedField Field { get; set; }
    public decimal OldValue { get; set; }
    public decimal NewValue { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAtUtc { get; set; }
    public ChangeReason Reason { get; set; }
    public string? Note { get; set; }
    public string? OrderId { get; set; }

    public static AuditDocument From(PriceStockChange a, string storeId) => new()
    {
        Id = IdPrefix + a.Id, DocType = DocTypes.Audit, StoreId = storeId, ProductId = a.ProductId, Field = a.Field,
        OldValue = a.OldValue, NewValue = a.NewValue, ChangedBy = a.ChangedBy, ChangedAtUtc = a.ChangedAtUtc,
        Reason = a.Reason, Note = a.Note, OrderId = a.OrderId
    };

    public PriceStockChange ToDomain() =>
        new(Id[IdPrefix.Length..], ProductId, Field, OldValue, NewValue, ChangedBy, ChangedAtUtc, Reason, Note, OrderId);
}

public sealed class OrderLineDocument
{
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string FranchiseCode { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal { get; set; }
    public ProductType? Type { get; set; }

    /// <summary>Photo shown when the order was placed (absent if the product had none).</summary>
    public string? ImageUrl { get; set; }
}

public sealed class ShippingAddressDocument
{
    public string FullName { get; set; } = "";
    public string Mobile { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PinCode { get; set; } = "";
}

public sealed class OrderDocument : CosmosDocument
{
    public string CustomerId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public OrderStatus Status { get; set; }
    public List<OrderLineDocument> Lines { get; set; } = [];
    public int ItemCount { get; set; }
    public decimal Subtotal { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Total { get; set; }
    public ShippingAddressDocument? ShippingAddress { get; set; }
    public PaymentMethod PaymentMethod { get; set; }

    public static OrderDocument From(Order o) => new()
    {
        ShippingAddress = o.ShippingAddress is not { } a ? null : new ShippingAddressDocument
        {
            FullName = a.FullName, Mobile = a.Mobile, Line1 = a.Line1, Line2 = a.Line2, City = a.City, State = a.State, PinCode = a.PinCode
        },
        PaymentMethod = o.PaymentMethod,
        Id = o.Id.ToString("D"), DocType = DocTypes.Order, CustomerId = o.CustomerId, CreatedAtUtc = o.CreatedAtUtc,
        Status = o.Status, ItemCount = o.Lines.Sum(l => l.Quantity), Subtotal = o.Subtotal, ShippingFee = o.ShippingFee, Total = o.Total,
        Lines = o.Lines.Select(l => new OrderLineDocument
        {
            ProductId = l.ProductId, ProductName = l.ProductName, FranchiseCode = l.FranchiseCode,
            UnitPrice = l.UnitPrice, Quantity = l.Quantity, LineTotal = l.LineTotal, Type = l.Type, ImageUrl = l.ImageUrl
        }).ToList()
    };

    public Order ToDomain() => Order.Rehydrate(Guid.Parse(Id), CustomerId, CreatedAtUtc, Status,
        Lines.Select(l => new OrderLine(l.ProductId, l.ProductName, l.FranchiseCode, l.UnitPrice, l.Quantity, l.Type, l.ImageUrl)).ToList(),
        Subtotal, ShippingFee, Total,
        ShippingAddress is not { } a ? null : new ShippingAddress(a.FullName, a.Mobile, a.Line1, a.Line2, a.City, a.State, a.PinCode),
        PaymentMethod);
}

/// <summary>Lives in the catalog partition next to the products it deducted, so it can be written in the same transaction.</summary>
public sealed class CheckoutCommitDocument : CosmosDocument
{
    public const string IdPrefix = "checkout:";

    /// <summary>Completed commits are kept for 30 days as a safety net for late retries, then expire to keep the partition small.</summary>
    public const int CompletedTtlSeconds = 30 * 24 * 3600;

    public string StoreId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public CommitStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public OrderDocument Order { get; set; } = new();

    public static string IdFor(Guid orderId) => IdPrefix + orderId.ToString("D");

    public static CheckoutCommitDocument From(CheckoutCommit c, string storeId) => new()
    {
        Id = IdFor(c.Order.Id), DocType = DocTypes.Checkout, StoreId = storeId, CustomerId = c.Order.CustomerId,
        Status = c.Status, CreatedAtUtc = c.Order.CreatedAtUtc, Order = OrderDocument.From(c.Order)
    };

    public CheckoutCommit ToDomain() => new(Order.ToDomain(), Status);
}

public sealed class CartLineDocument
{
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string FranchiseCode { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public DateTime AddedAtUtc { get; set; }
}

public sealed class CartDocument : CosmosDocument
{
    public const string FixedId = "cart";

    public string CustomerId { get; set; } = "";
    public List<CartLineDocument> Lines { get; set; } = [];
    public List<Guid> AppliedOrderIds { get; set; } = [];
    public DateTime UpdatedAtUtc { get; set; }

    public static CartDocument From(Cart c) => new()
    {
        Id = FixedId, DocType = DocTypes.Cart, CustomerId = c.CustomerId, UpdatedAtUtc = c.UpdatedAtUtc,
        AppliedOrderIds = c.AppliedOrderIds.ToList(),
        Lines = c.Lines.Select(l => new CartLineDocument
        {
            ProductId = l.ProductId, ProductName = l.ProductName, FranchiseCode = l.FranchiseCode,
            UnitPrice = l.UnitPrice, Quantity = l.Quantity, AddedAtUtc = l.AddedAtUtc
        }).ToList()
    };

    public Cart ToDomain() => Cart.Rehydrate(CustomerId,
        Lines.Select(l => new CartLine
        {
            ProductId = l.ProductId, ProductName = l.ProductName, FranchiseCode = l.FranchiseCode,
            UnitPrice = l.UnitPrice, Quantity = l.Quantity, AddedAtUtc = l.AddedAtUtc
        }),
        AppliedOrderIds, UpdatedAtUtc, ETag);
}

public sealed class IdempotencyDocument : CosmosDocument
{
    public const string IdPrefix = "idem:";

    /// <summary>Clients retry within seconds or minutes; 24h is generous and keeps the partition small.</summary>
    public const int TtlSeconds = 24 * 3600;

    public string CustomerId { get; set; } = "";
    public string RequestHash { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }

    public static string IdFor(string key) => IdPrefix + key;

    public static IdempotencyDocument From(IdempotencyRecord r) => new()
    {
        Id = IdFor(r.Key), DocType = DocTypes.Idempotency, CustomerId = r.CustomerId, RequestHash = r.RequestHash,
        Response = r.ResponseJson, CreatedAtUtc = r.CreatedAtUtc, Ttl = TtlSeconds
    };

    public IdempotencyRecord ToDomain() => new(Id[IdPrefix.Length..], CustomerId, RequestHash, Response, CreatedAtUtc);
}

public sealed class UserDocument : CosmosDocument
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public List<string> Roles { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The id is a hash of the email: point reads on login, and no email addresses in ids, URLs or diagnostics.</summary>
    public static string IdFor(string normalizedEmail) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedEmail))).ToLowerInvariant();

    public static UserDocument From(User u) => new()
    {
        Id = IdFor(u.NormalizedEmail), DocType = DocTypes.User, UserId = u.Id, Email = u.Email, NormalizedEmail = u.NormalizedEmail,
        DisplayName = u.DisplayName, PasswordHash = u.PasswordHash, Roles = u.Roles.ToList(), CreatedAtUtc = u.CreatedAtUtc
    };

    public User ToDomain() => new()
    {
        Id = UserId, Email = Email, NormalizedEmail = NormalizedEmail, DisplayName = DisplayName,
        PasswordHash = PasswordHash, Roles = Roles, CreatedAtUtc = CreatedAtUtc
    };
}
