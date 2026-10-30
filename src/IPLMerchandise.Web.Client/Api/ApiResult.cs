using System.Net;
using System.Text.Json;
using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Web.Client.Api;

/// <summary>An RFC 9457 problem from the API (or a synthetic one for network failures), in a shape the UI can render.</summary>
public sealed record ApiProblem
{
    public int Status { get; init; }
    public string Title { get; init; } = "Something went wrong";
    public string? Detail { get; init; }
    public string? Code { get; init; }
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
    public IReadOnlyList<CheckoutFailureLine> FailingLines { get; init; } = [];
    public string? CorrelationId { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public string? CurrentETag { get; init; }

    public string Message => Detail ?? Title;

    /// <summary>Nothing reached the server or no answer came back: the outcome is unknown, so retry with the same key.</summary>
    public bool IsNetworkError => Status == 0;
    public bool IsTransient => IsNetworkError || Status is 408 or 429 or 502 or 503 or 504;
    public bool IsConcurrencyConflict => Status == (int)HttpStatusCode.PreconditionFailed;
    public bool IsUnauthorized => Status == (int)HttpStatusCode.Unauthorized;

    public static ApiProblem Network(string? detail = null) => new()
    {
        Status = 0,
        Code = "network-error",
        Title = "Connection problem",
        Detail = detail ?? "We could not reach the store. Check your connection and try again."
    };

    internal static ApiProblem FromJson(int status, JsonElement json, int? retryAfter)
    {
        string? Str(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (json.TryGetProperty(ProblemExtensions.Errors, out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                errors[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];

        var failing = json.TryGetProperty(ProblemExtensions.FailingLines, out var f) && f.ValueKind == JsonValueKind.Array
            ? f.Deserialize<List<CheckoutFailureLine>>(ApiJson.Options) ?? []
            : [];

        return new ApiProblem
        {
            Status = status,
            Title = Str("title") ?? "Something went wrong",
            Detail = Str("detail"),
            Code = Str(ProblemExtensions.Code),
            Errors = errors,
            FailingLines = failing,
            CorrelationId = Str(ProblemExtensions.CorrelationId),
            RetryAfterSeconds = retryAfter ?? (json.TryGetProperty(ProblemExtensions.RetryAfterSeconds, out var r) && r.TryGetInt32(out var s) ? s : null),
            CurrentETag = Str(ProblemExtensions.CurrentETag)
        };
    }
}

/// <summary>Either a value or a problem. API clients never throw for HTTP or network failures; pages branch on this.</summary>
public sealed class ApiResult<T>
{
    private ApiResult() { }

    public T? Value { get; private init; }
    public ApiProblem? Problem { get; private init; }
    public bool IsSuccess => Problem is null;
    public int StatusCode { get; private init; }
    public string? ETag { get; private init; }

    /// <summary>The server recognised the idempotency key / order id and returned the original result.</summary>
    public bool Replayed { get; private init; }

    public static ApiResult<T> Ok(T value, int status = 200, string? etag = null, bool replayed = false) =>
        new() { Value = value, StatusCode = status, ETag = etag, Replayed = replayed };

    public static ApiResult<T> Fail(ApiProblem problem) => new() { Problem = problem, StatusCode = problem.Status };
}
