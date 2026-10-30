namespace IPLMerchandise.Contracts.V1;

/// <summary>
/// A page of results. <see cref="ContinuationToken"/> is opaque: pass it back unchanged to get the next page; null means no more pages.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int PageSize, string? ContinuationToken, long? TotalCount = null)
{
    public bool HasMore => ContinuationToken is not null;
}

public static class ApiHeaders
{
    /// <summary>Client-generated key on every cart mutation. Replays return the original result.</summary>
    public const string IdempotencyKey = "Idempotency-Key";
    public const string CorrelationId = "X-Correlation-ID";
    public const string IdempotentReplay = "Idempotent-Replay";
}

public static class ApiLimits
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxSearchLength = 100;

    /// <summary>Largest product photo accepted by PUT /products/{id}/image (2 MB).</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>Photo formats the API accepts. Checked against the file's content, not only its declared type.</summary>
    public static readonly IReadOnlyList<string> ImageContentTypes = ["image/jpeg", "image/png", "image/webp"];
}
