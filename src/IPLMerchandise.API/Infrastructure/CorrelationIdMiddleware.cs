using System.Diagnostics;
using System.Text.RegularExpressions;
using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.API.Infrastructure;

/// <summary>
/// Accepts the caller's X-Correlation-ID (if well-formed) or creates one, echoes it on the response, puts it in the
/// logging scope of everything that happens for this request, and tags the trace with it.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[ApiHeaders.CorrelationId].ToString();
        // Never trust arbitrary header content into logs: accept only a short, safe token.
        var correlationId = SafeId().IsMatch(incoming) ? incoming : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[ApiHeaders.CorrelationId] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { [ItemKey] = correlationId }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) => context.Items.TryGetValue(ItemKey, out var v) ? v as string : null;

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}
