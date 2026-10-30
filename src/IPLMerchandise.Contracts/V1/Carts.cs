using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Contracts.V1;

public sealed record AddCartItemRequest
{
    [Required, StringLength(64, MinimumLength = 1)]
    public string ProductId { get; set; } = "";

    [Required, Range(1, 10)]
    public int? Quantity { get; set; }
}

public sealed record UpdateCartItemRequest
{
    [Required, Range(1, 10)]
    public int? Quantity { get; set; }
}

public enum CartLineIssue { PriceChanged, InsufficientStock, OutOfStock, Unavailable }

public sealed record CartLineDto
{
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public required string FranchiseCode { get; init; }

    /// <summary>Added later in v1 (optional): franchise display name and product type, for richer cart rows. Null if the product no longer exists.</summary>
    public string? FranchiseName { get; init; }
    public ProductType? Type { get; init; }

    /// <summary>Added later in v1 (optional): the product's current photo, if it has one.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Price the customer accepted when adding the line.</summary>
    public required decimal UnitPrice { get; init; }

    /// <summary>Live catalogue price; null if the product no longer exists.</summary>
    public decimal? CurrentUnitPrice { get; set; }
    public required int Quantity { get; init; }
    public required int AvailableStock { get; init; }
    public required int MaxQuantity { get; init; }
    public required decimal LineTotal { get; init; }
    public required IReadOnlyList<CartLineIssue> Issues { get; init; }
}

public sealed record CartDto
{
    public required IReadOnlyList<CartLineDto> Lines { get; init; }
    public required int ItemCount { get; init; }
    public required decimal Subtotal { get; init; }
    public required decimal ShippingFee { get; init; }
    public required decimal Total { get; init; }

    /// <summary>True when a price or stock level changed since lines were added. Checkout is blocked until changes are accepted.</summary>
    public required bool RequiresReview { get; init; }

    /// <summary>Total the customer will pay after accepting the changes (equals <see cref="Total"/> when nothing changed).</summary>
    public required decimal TotalAfterAccept { get; init; }
}
