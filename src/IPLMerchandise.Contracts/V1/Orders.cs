using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Contracts.V1;

public sealed record CheckoutRequest
{
    /// <summary>Client-generated. Retrying with the same ID returns the same order and never creates a second one.</summary>
    [Required]
    public Guid? OrderId { get; set; }

    /// <summary>The total the customer saw and agreed to. If the server computes a different total, checkout is refused (409).</summary>
    [Required, Range(typeof(decimal), "0.01", "100000000")]
    public decimal? ExpectedTotal { get; set; }

    /// <summary>
    /// Added later in v1 (optional, so earlier clients keep working): where to deliver. The web client always sends it.
    /// </summary>
    public ShippingAddressDto? ShippingAddress { get; set; }

    /// <summary>Added later in v1 (optional). Only cash on delivery is supported; omitted means cash on delivery.</summary>
    public PaymentMethod? PaymentMethod { get; set; }
}

/// <summary>Only cash on delivery exists today; online payment is out of scope (see README).</summary>
public enum PaymentMethod { CashOnDelivery }

/// <summary>An Indian delivery address. Validation mirrors what couriers need.</summary>
public sealed record ShippingAddressDto
{
    [Required, StringLength(80, MinimumLength = 2)]
    public string FullName { get; set; } = "";

    /// <summary>10-digit Indian mobile number without +91.</summary>
    [Required, RegularExpression("^[6-9][0-9]{9}$", ErrorMessage = "Enter a 10-digit mobile number starting with 6, 7, 8 or 9.")]
    public string Mobile { get; set; } = "";

    [Required, StringLength(120, MinimumLength = 3)]
    public string Line1 { get; set; } = "";

    [StringLength(120)]
    public string? Line2 { get; set; }

    [Required, StringLength(60, MinimumLength = 2)]
    public string City { get; set; } = "";

    /// <summary>One of <see cref="IndianStates.All"/>.</summary>
    [Required]
    public string State { get; set; } = "";

    [Required, RegularExpression("^[1-9][0-9]{5}$", ErrorMessage = "Enter a valid 6-digit PIN code.")]
    public string PinCode { get; set; } = "";
}

/// <summary>States and union territories, shared by the API (validation) and the web client (drop-down).</summary>
public static class IndianStates
{
    public static readonly IReadOnlyList<string> All =
    [
        "Andaman and Nicobar Islands", "Andhra Pradesh", "Arunachal Pradesh", "Assam", "Bihar", "Chandigarh", "Chhattisgarh",
        "Dadra and Nagar Haveli and Daman and Diu", "Delhi", "Goa", "Gujarat", "Haryana", "Himachal Pradesh", "Jammu and Kashmir",
        "Jharkhand", "Karnataka", "Kerala", "Ladakh", "Lakshadweep", "Madhya Pradesh", "Maharashtra", "Manipur", "Meghalaya",
        "Mizoram", "Nagaland", "Odisha", "Puducherry", "Punjab", "Rajasthan", "Sikkim", "Tamil Nadu", "Telangana", "Tripura",
        "Uttar Pradesh", "Uttarakhand", "West Bengal"
    ];
}

public enum OrderStatus { Confirmed }

public sealed record OrderLineDto(string ProductId, string ProductName, string FranchiseCode, decimal UnitPrice, int Quantity, decimal LineTotal)
{
    /// <summary>Added later in v1 (optional): null for orders placed before it was recorded.</summary>
    public ProductType? Type { get; init; }

    /// <summary>Added later in v1 (optional): display name of the franchise.</summary>
    public string? FranchiseName { get; init; }

    /// <summary>Added later in v1 (optional): the photo the product had when the order was placed.</summary>
    public string? ImageUrl { get; init; }
}

public sealed record OrderDto(
    Guid Id,
    DateTime CreatedAtUtc,
    OrderStatus Status,
    IReadOnlyList<OrderLineDto> Lines,
    decimal Subtotal,
    decimal ShippingFee,
    decimal Total)
{
    /// <summary>Added later in v1 (optional): null for orders placed without an address.</summary>
    public ShippingAddressDto? ShippingAddress { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
}

public sealed record OrderSummaryDto(Guid Id, DateTime CreatedAtUtc, OrderStatus Status, int ItemCount, decimal Total)
{
    /// <summary>Added later in v1 (optional): the products in the order, for thumbnails in the order list.</summary>
    public IReadOnlyList<OrderItemPreviewDto>? Items { get; init; }
}

public sealed record OrderItemPreviewDto(string ProductName, string FranchiseCode, ProductType? Type, int Quantity)
{
    /// <summary>Added later in v1 (optional): the photo the product had when the order was placed.</summary>
    public string? ImageUrl { get; init; }
}

/// <summary>Headline numbers for the customer's order history.</summary>
public sealed record OrderStatsDto(int TotalOrders, decimal SpentLast30Days, decimal TotalSpent);

/// <summary>One line that stopped a checkout. Returned in the "failingLines" problem-details extension.</summary>
public sealed record CheckoutFailureLine(string ProductId, string ProductName, int Requested, int Available, string Reason);
