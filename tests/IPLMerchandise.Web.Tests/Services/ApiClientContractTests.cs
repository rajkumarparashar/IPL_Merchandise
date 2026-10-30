using System.Net;
using System.Text;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Api;
using IPLMerchandise.Web.Client.Formatting;

namespace IPLMerchandise.Web.Tests.Services;

/// <summary>The typed clients speak the API's contract: routes, headers, and problem-details parsing.</summary>
public class ApiClientContractTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, mediaType) };

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://api.test/") };

    [Fact]
    public async Task Cart_add_posts_to_the_v1_route_with_an_idempotency_key()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.OK, """{"lines":[],"itemCount":0,"subtotal":0,"shippingFee":0,"total":0,"requiresReview":false,"totalAfterAccept":0}"""));
        await new CartApi(Client(stub)).AddAsync("csk-cap", 3, "key-123");

        var request = stub.Requests.Single();
        Assert.Equal((HttpMethod.Post, "/api/v1/cart/items"), (request.Method, request.RequestUri!.AbsolutePath));
        Assert.Equal("key-123", request.Headers.GetValues(ApiHeaders.IdempotencyKey).Single());
    }

    [Fact]
    public async Task Problem_details_are_parsed_including_failing_lines()
    {
        const string problem = """
            {"title":"Conflict","status":409,"detail":"Some items cannot be supplied.","code":"checkout-rejected","correlationId":"abc",
             "failingLines":[{"productId":"kkr","productName":"KKR Signed Photo","requested":1,"available":0,"reason":"out-of-stock"}]}
            """;
        var stub = new StubHandler(_ => Json(HttpStatusCode.Conflict, problem, "application/problem+json"));

        var p = (await new OrdersApi(Client(stub)).CheckoutAsync(Guid.NewGuid(), 10m)).Problem!;

        Assert.Equal((409, "checkout-rejected", "abc"), (p.Status, p.Code, p.CorrelationId));
        Assert.Equal("KKR Signed Photo", p.FailingLines.Single().ProductName);
    }

    [Fact]
    public async Task Admin_updates_send_if_match_and_412_is_recognised()
    {
        var stub = new StubHandler(_ => Json(HttpStatusCode.PreconditionFailed, """{"status":412,"title":"Precondition failed","code":"concurrency-conflict"}""", "application/problem+json"));
        var result = await new AdminApi(Client(stub)).SetStockAsync("csk-cap", new SetStockRequest { StockQuantity = 1, Reason = "Recount" }, "\"v1\"");

        Assert.Equal("\"v1\"", stub.Requests.Single().Headers.GetValues("If-Match").Single());
        Assert.True(result.Problem!.IsConcurrencyConflict);
    }

    [Fact]
    public async Task Transient_failures_are_retried_only_for_idempotent_requests()
    {
        var calls = 0;
        var inner = new StubHandler(_ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(HttpStatusCode.OK, "{}"));
        var client = new HttpClient(new RetryHandler { InnerHandler = inner, Delay = (_, _) => Task.CompletedTask });

        var safe = new HttpRequestMessage(HttpMethod.Post, "http://api.test/api/v1/cart/items") { Content = new StringContent("{}") };
        safe.Options.Set(RetryHandler.SafeToRetry, true);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(safe)).StatusCode);
        Assert.Equal(3, calls);

        calls = 0;
        var notSafe = new HttpRequestMessage(HttpMethod.Post, "http://api.test/api/v1/auth/login") { Content = new StringContent("{}") };
        await client.SendAsync(notSafe);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(499, "₹499.00")]
    [InlineData(10494, "₹10,494.00")]
    [InlineData(100000, "₹1,00,000.00")]
    public void Rupees_use_two_decimals_and_indian_grouping(decimal amount, string expected) =>
        Assert.Equal(expected, Money.Format(amount));
}
