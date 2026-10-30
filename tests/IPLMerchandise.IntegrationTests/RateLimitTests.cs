using System.Net;
using IPLMerchandise.IntegrationTests.Infrastructure;

namespace IPLMerchandise.IntegrationTests;

public sealed class LowLimitFactory : ApiFactory
{
    protected override IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?> { ["RateLimiting:SearchPerMinute"] = "3" };
}

public class RateLimitTests(LowLimitFactory factory) : IClassFixture<LowLimitFactory>
{
    [Fact]
    public async Task Search_over_the_limit_is_429_with_retry_after()
    {
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++) await client.GetAsync("/api/v1/products");

        var limited = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("rate-limited", await TestClient.CodeAsync(limited));
    }
}
