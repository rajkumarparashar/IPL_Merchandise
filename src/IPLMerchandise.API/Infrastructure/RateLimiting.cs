using System.Globalization;
using System.Threading.RateLimiting;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace IPLMerchandise.API.Infrastructure;

public sealed class RateLimitOptions
{
    public const string Section = "RateLimiting";
    public int SearchPerMinute { get; set; } = 120;
    public int CheckoutPerMinute { get; set; } = 10;
    public int AuthPerMinute { get; set; } = 10;
}

public static class RateLimitPolicies
{
    public const string Search = "search";
    public const string Checkout = "checkout";
    public const string Auth = "auth";

    public static IServiceCollection AddStoreRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));

        // Limits are read per request (IOptionsMonitor), so configuration changes apply without a restart.
        static RateLimitOptions Limits(HttpContext ctx) =>
            ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<RateLimitOptions>>().CurrentValue;

        return services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(Search, ctx => Window(ClientKey(ctx), Limits(ctx).SearchPerMinute));
            limiter.AddPolicy(Checkout, ctx => Window(ClientKey(ctx), Limits(ctx).CheckoutPerMinute));
            // Brute-force protection for login/register: keyed by IP because the caller is not authenticated yet.
            limiter.AddPolicy(Auth, ctx => Window("ip:" + ctx.Connection.RemoteIpAddress, Limits(ctx).AuthPerMinute));

            limiter.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? (int)Math.Ceiling(ra.TotalSeconds) : 60;
                var http = context.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

                var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problems.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = 429,
                        Title = "Too many requests",
                        Detail = $"Rate limit exceeded. Retry after {retryAfter} seconds.",
                        Extensions =
                        {
                            [ProblemExtensions.Code] = ProblemCodes.RateLimited,
                            [ProblemExtensions.RetryAfterSeconds] = retryAfter
                        }
                    }
                });
            };
        });
    }

    /// <summary>Signed-in callers are limited per user (fair behind shared NAT); anonymous callers per IP.</summary>
    private static string ClientKey(HttpContext ctx) =>
        ctx.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is { } sub ? "user:" + sub : "ip:" + ctx.Connection.RemoteIpAddress;

    private static RateLimitPartition<string> Window(string key, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
}
