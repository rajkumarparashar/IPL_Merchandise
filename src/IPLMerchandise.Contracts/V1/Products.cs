using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Contracts.V1;

public enum ProductType { Jersey, Cap, Flag, SignedPhotograph, Other }

public enum StockStatus { InStock, LowStock, OutOfStock }

public sealed record FranchiseDto(string Code, string Name);

/// <summary>Product list order. Added in v1 as an optional query parameter; omitted = Featured (the original order).</summary>
public enum ProductSort { Featured, PriceLowToHigh, PriceHighToLow, NameAToZ }

public sealed record TypeFacet(ProductType Type, int Count);

public sealed record FranchiseFacet(string Code, string Name, int Count);

/// <summary>How many active products of each type and franchise match the current search text.</summary>
public sealed record ProductFacetsDto(IReadOnlyList<TypeFacet> Types, IReadOnlyList<FranchiseFacet> Franchises);

public sealed record ProductDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ProductType Type { get; init; }
    public required string FranchiseCode { get; init; }
    public required string FranchiseName { get; init; }
    public required string Description { get; init; }

    /// <summary>Tax-inclusive price in INR, at most two decimal places.</summary>
    public required decimal Price { get; init; }
    public required int StockQuantity { get; init; }
    public required StockStatus StockStatus { get; init; }
    public required bool IsLimitedEdition { get; init; }
    public required bool IsActive { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }

    /// <summary>Concurrency token. Send it back in If-Match when changing the product.</summary>
    public required string ETag { get; init; }

    /// <summary>
    /// Added in v1 after first release (backwards-compatible change): the most a customer may put on one cart line.
    /// Optional and nullable, so older clients that do not know it simply ignore it.
    /// </summary>
    public int? MaxQuantityPerLine { get; set; }

    /// <summary>Added later in v1 (optional): promoted on the home page.</summary>
    public bool? IsFeatured { get; set; }

    /// <summary>
    /// Added later in v1 (optional): absolute URL of the product photo, or null when it has none
    /// (clients then show their own placeholder). Set with PUT /products/{id}/image.
    /// </summary>
    public string? ImageUrl { get; set; }
}

public sealed record CreateProductRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required]
    public ProductType? Type { get; set; }

    [Required, StringLength(8, MinimumLength = 1)]
    public string FranchiseCode { get; set; } = "";

    [StringLength(2000)]
    public string Description { get; set; } = "";

    [Required, Range(typeof(decimal), "0.01", "1000000")]
    public decimal? Price { get; set; }

    [Required, Range(0, 100_000)]
    public int? StockQuantity { get; set; }

    public bool IsFeatured { get; set; }
}

public sealed record UpdateProductRequest
{
    [Required, StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required]
    public ProductType? Type { get; set; }

    [Required, StringLength(8, MinimumLength = 1)]
    public string FranchiseCode { get; set; } = "";

    [StringLength(2000)]
    public string Description { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Optional: null leaves the featured flag unchanged, so older clients cannot clear it by accident.</summary>
    public bool? IsFeatured { get; set; }
}

public sealed record ChangePriceRequest
{
    [Required, Range(typeof(decimal), "0.01", "1000000")]
    public decimal? Price { get; set; }

    [Required, StringLength(200, MinimumLength = 3)]
    public string Reason { get; set; } = "";
}

public sealed record SetStockRequest
{
    [Required, Range(0, 100_000)]
    public int? StockQuantity { get; set; }

    [Required, StringLength(200, MinimumLength = 3)]
    public string Reason { get; set; } = "";
}

public enum AuditField { Price, Stock }

public enum AuditReason { ProductCreated, ManualAdjustment, Order }

public sealed record AuditEntryDto(
    string Id,
    string ProductId,
    AuditField Field,
    decimal OldValue,
    decimal NewValue,
    string ChangedBy,
    DateTime ChangedAtUtc,
    AuditReason Reason,
    string? Note,
    string? OrderId);
