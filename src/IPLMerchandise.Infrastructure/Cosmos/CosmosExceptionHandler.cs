using System.Net;
using IPLMerchandise.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Infrastructure.Cosmos;

/// <summary>
/// Turns transient database failures (throttling that outlasted the SDK's retries, timeouts, unavailability) into
/// 503 + Retry-After so clients back off and retry, instead of a generic 500. Every write is idempotent, so retrying is safe.
/// </summary>
public sealed class CosmosExceptionHandler(IProblemDetailsService problemDetails, ILogger<CosmosExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        TimeSpan? retryAfter;
        switch (exception)
        {
            case CosmosException { StatusCode: HttpStatusCode.TooManyRequests } ce:
                retryAfter = ce.RetryAfter ?? TimeSpan.FromSeconds(1);
                break;
            case CosmosException { StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout or HttpStatusCode.Gone }:
            case CosmosOperationCanceledException when !context.RequestAborted.IsCancellationRequested:
            case HttpRequestException:
                retryAfter = TimeSpan.FromSeconds(2);
                break;
            default:
                return false;
        }

        logger.LogWarning(exception, "Database dependency unavailable");
        var seconds = (int)Math.Ceiling(retryAfter.Value.TotalSeconds);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = seconds.ToString();
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service temporarily unavailable",
                Detail = "The store is busy right now. Please retry shortly.",
                Extensions = { ["code"] = ErrorCodes.DependencyUnavailable, ["retryAfterSeconds"] = seconds }
            }
        });
    }
}
