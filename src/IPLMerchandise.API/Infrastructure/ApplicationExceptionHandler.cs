using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace IPLMerchandise.API.Infrastructure;

/// <summary>
/// Single place that maps business and application errors to HTTP status codes and RFC 9457 problem details.
/// Controllers never catch exceptions themselves.
/// </summary>
public sealed class ApplicationExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApplicationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            // The client went away. Nothing to report.
            context.Response.StatusCode = 499;
            return true;
        }

        var problem = Map(exception, context);
        if (problem is null) return false;

        if (problem.Status >= 500) logger.LogError(exception, "Unhandled error");
        else logger.LogInformation("Request rejected: {Status} {Code}", problem.Status, problem.Extensions.TryGetValue(ProblemExtensions.Code, out var code) ? code : null);

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }

    private static ProblemDetails? Map(Exception exception, HttpContext context)
    {
        switch (exception)
        {
            case RequestValidationException e:
                return WithCode(new ValidationProblemDetails(e.Errors) { Status = 400, Title = "Validation failed", Detail = e.Message }, e.Code);

            case BusinessRuleException e:
                var errors = new Dictionary<string, string[]> { [e.Field ?? "request"] = [e.Message] };
                return WithCode(new ValidationProblemDetails(errors) { Status = 400, Title = "Business rule violated", Detail = e.Message }, e.Code);

            case AuthenticationFailedException e:
                return WithCode(new ProblemDetails { Status = 401, Title = "Authentication failed", Detail = e.Message }, e.Code);

            case NotFoundException e:
                return WithCode(new ProblemDetails { Status = 404, Title = "Not found", Detail = e.Message }, e.Code);

            case CartLineNotFoundException e:
                return WithCode(new ProblemDetails { Status = 404, Title = "Not found", Detail = e.Message }, e.Code);

            case InsufficientStockException e:
                var stock = WithCode(new ProblemDetails { Status = 409, Title = "Not enough stock", Detail = e.Message }, e.Code);
                stock.Extensions["productId"] = e.ProductId;
                stock.Extensions["requested"] = e.Requested;
                stock.Extensions["available"] = e.Available;
                return stock;

            case ProductUnavailableException e:
                var unavailable = WithCode(new ProblemDetails { Status = 409, Title = "Product unavailable", Detail = e.Message }, e.Code);
                unavailable.Extensions["productId"] = e.ProductId;
                return unavailable;

            case ConflictException e:
                var conflict = WithCode(new ProblemDetails { Status = 409, Title = "Conflict", Detail = e.Message }, e.Code);
                foreach (var (key, value) in e.Extensions) conflict.Extensions[key] = value;
                return conflict;

            case ConcurrencyConflictException e:
                var stale = WithCode(new ProblemDetails { Status = 412, Title = "Precondition failed", Detail = e.Message }, e.Code);
                if (e.CurrentETag is not null)
                {
                    stale.Extensions[ProblemExtensions.CurrentETag] = e.CurrentETag;
                    context.Response.Headers.ETag = e.CurrentETag;
                }
                return stale;

            case DependencyUnavailableException e:
                context.Response.Headers.RetryAfter = "2";
                var unavailableDependency = WithCode(new ProblemDetails { Status = 503, Title = "Service temporarily unavailable", Detail = e.Message }, e.Code);
                unavailableDependency.Extensions[ProblemExtensions.RetryAfterSeconds] = 2;
                return unavailableDependency;

            case PreconditionRequiredException e:
                return WithCode(new ProblemDetails { Status = 428, Title = "Precondition required", Detail = e.Message }, e.Code);

            case DomainException e:
                return WithCode(new ProblemDetails { Status = 409, Title = "Conflict", Detail = e.Message }, e.Code);

            default:
                return null; // Falls through to CosmosExceptionHandler, then the default 500 handler.
        }
    }

    private static T WithCode<T>(T problem, string code) where T : ProblemDetails
    {
        problem.Extensions[ProblemExtensions.Code] = code;
        return problem;
    }
}
