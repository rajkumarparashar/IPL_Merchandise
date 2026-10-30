namespace IPLMerchandise.Contracts.V1;

/// <summary>
/// Values of the "code" extension on problem-details responses. Part of the public contract: clients branch on these.
/// Kept in sync with IPLMerchandise.Domain.Common.ErrorCodes by a unit test.
/// </summary>
public static class ProblemCodes
{
    public const string ValidationFailed = "validation-failed";
    public const string InvalidPrice = "invalid-price";
    public const string InvalidStock = "invalid-stock";
    public const string InvalidQuantity = "invalid-quantity";
    public const string InvalidProduct = "invalid-product";
    public const string InvalidImage = "invalid-image";
    public const string InsufficientStock = "insufficient-stock";
    public const string ProductUnavailable = "product-unavailable";
    public const string CartLineLimit = "cart-line-limit";
    public const string CartEmpty = "cart-empty";
    public const string CartRequiresReview = "cart-requires-review";
    public const string TotalMismatch = "total-mismatch";
    public const string CheckoutRejected = "checkout-rejected";
    public const string OrderIdConflict = "order-id-conflict";
    public const string IdempotencyKeyReused = "idempotency-key-reused";
    public const string ConcurrencyConflict = "concurrency-conflict";
    public const string PreconditionRequired = "precondition-required";
    public const string NotFound = "not-found";
    public const string EmailInUse = "email-in-use";
    public const string InvalidCredentials = "invalid-credentials";
    public const string InvalidContinuationToken = "invalid-continuation-token";
    public const string RateLimited = "rate-limited";
    public const string DependencyUnavailable = "dependency-unavailable";
}

/// <summary>Names of extension members on problem-details responses.</summary>
public static class ProblemExtensions
{
    public const string Code = "code";
    public const string Errors = "errors";
    public const string FailingLines = "failingLines";
    public const string CorrelationId = "correlationId";
    public const string RetryAfterSeconds = "retryAfterSeconds";
    public const string CurrentETag = "currentETag";
}
