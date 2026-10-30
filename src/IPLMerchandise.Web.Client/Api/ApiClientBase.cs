using System.Net.Http.Json;
using System.Text.Json;
using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Web.Client.Api;

/// <summary>
/// Shared plumbing for the typed clients: sends, maps success to the DTO and every failure (HTTP or network) to an
/// <see cref="ApiProblem"/>. Components never see HttpClient or raw JSON.
/// </summary>
public abstract class ApiClientBase(HttpClient http)
{
    protected HttpClient Http { get; } = http;

    protected Task<ApiResult<T>> GetAsync<T>(string url, CancellationToken ct = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, url), ct);

    protected static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? idempotencyKey = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(ApiHeaders.IdempotencyKey, idempotencyKey);
            request.Options.Set(RetryHandler.SafeToRetry, true);
        }
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    protected async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            return ApiResult<T>.Fail(ApiProblem.Network());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(ApiProblem.Network("The store took too long to answer. Your request may still have gone through; retrying is safe."));
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct);
                return ApiResult<T>.Ok(value!, status, response.Headers.ETag?.ToString(),
                    response.Headers.TryGetValues(ApiHeaders.IdempotentReplay, out _));
            }

            var retryAfter = response.Headers.RetryAfter?.Delta is { } d ? (int)Math.Ceiling(d.TotalSeconds) : (int?)null;
            try
            {
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                return ApiResult<T>.Fail(ApiProblem.FromJson(status, json, retryAfter));
            }
            catch (JsonException)
            {
                return ApiResult<T>.Fail(new ApiProblem { Status = status, Title = response.ReasonPhrase ?? "Request failed", RetryAfterSeconds = retryAfter });
            }
        }
    }
}
