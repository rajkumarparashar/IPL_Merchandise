namespace IPLMerchandise.Domain.Common;

/// <summary>Base type for violations of business rules. <see cref="Code"/> is a stable, machine-readable identifier.</summary>
public abstract class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The request itself breaks a rule (bad quantity, invalid price). Maps to 400.</summary>
public sealed class BusinessRuleException(string code, string message, string? field = null) : DomainException(code, message)
{
    public string? Field { get; } = field;
}

/// <summary>The request is valid but the current stock cannot satisfy it. Maps to 409.</summary>
public sealed class InsufficientStockException(string productId, string productName, int requested, int available)
    : DomainException(ErrorCodes.InsufficientStock,
        available == 0
            ? $"'{productName}' is out of stock."
            : $"Only {available} of '{productName}' available; {requested} requested.")
{
    public string ProductId { get; } = productId;
    public string ProductName { get; } = productName;
    public int Requested { get; } = requested;
    public int Available { get; } = available;
}

/// <summary>The product exists but cannot be sold right now (disabled by an admin). Maps to 409.</summary>
public sealed class ProductUnavailableException(string productId, string productName)
    : DomainException(ErrorCodes.ProductUnavailable, $"'{productName}' is no longer available.")
{
    public string ProductId { get; } = productId;
}
