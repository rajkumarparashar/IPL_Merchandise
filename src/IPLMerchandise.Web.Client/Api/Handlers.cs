using System.Net;
using System.Net.Http.Headers;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Auth;

namespace IPLMerchandise.Web.Client.Api;

/// <summary>Adds the bearer token, and tells the app when the API says the session is no longer valid.</summary>
public sealed class AuthHeaderHandler(TokenStore tokens, SessionEvents session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetAsync();
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null)
            session.RaiseSessionExpired();
        return response;
    }
}

/// <summary>Every request carries its own correlation id, so a failure the user reports can be found in server logs.</summary>
public sealed class CorrelationIdHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation(ApiHeaders.CorrelationId, Guid.NewGuid().ToString("N"));
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// Retries transient failures (network errors, 502/503/504) with exponential backoff and jitter.
/// Only for requests that are safe to repeat: GET, or writes that carry an idempotency key / order id.
/// Business answers (400, 409, 412) are never retried: repeating them cannot change the outcome.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> SafeToRetry = new("SafeToRetry");
    public const int MaxAttempts = 3;

    /// <summary>Overridable in tests to avoid real waiting.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var retryable = request.Method == HttpMethod.Get || (request.Options.TryGetValue(SafeToRetry, out var safe) && safe);
        if (!retryable) return await base.SendAsync(request, ct);

        if (request.Content is not null) await request.Content.LoadIntoBufferAsync(ct); // so the body can be re-sent

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, ct);
                if (attempt >= MaxAttempts || response.StatusCode is not (HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout))
                    return response;
                var wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
                response.Dispose();
                await Delay(wait, ct);
            }
            catch (HttpRequestException) when (attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                await Delay(Backoff(attempt), ct);
            }
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 200));
}
