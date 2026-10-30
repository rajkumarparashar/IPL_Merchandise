using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.IntegrationTests.Infrastructure;

/// <summary>Small typed helper over HttpClient so tests read like the scenario they describe.</summary>
public sealed class TestClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public HttpClient Http { get; } = http;

    public static async Task<TestClient> CustomerAsync(ApiFactory factory, string? name = null)
    {
        var client = new TestClient(factory.CreateClient());
        var email = $"{name ?? "user"}-{Guid.NewGuid():N}@tests.local";
        var response = await client.Http.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest { Email = email, Password = "Passw0rd!", DisplayName = name ?? "Test" }, Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.UserId = auth.User.Id;
        return client;
    }

    public static async Task<TestClient> AdminAsync(ApiFactory factory)
    {
        var client = new TestClient(factory.CreateClient());
        var response = await client.Http.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { Email = ApiFactory.AdminEmail, Password = ApiFactory.AdminPassword }, Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    public string UserId { get; private set; } = "";

    public static string NewKey() => Guid.NewGuid().ToString("N");

    // ---- catalogue / admin ----
    public async Task<ProductDto> CreateProductAsync(string name, decimal price, int stock,
        ProductType type = ProductType.Jersey, string franchise = "MI")
    {
        var response = await Http.PostAsJsonAsync("/api/v1/products", new CreateProductRequest
        {
            Name = name, Type = type, FranchiseCode = franchise, Description = "test", Price = price, StockQuantity = stock
        }, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDto>(Json))!;
    }

    public async Task<ProductDto> GetProductAsync(string id) =>
        (await Http.GetFromJsonAsync<ProductDto>($"/api/v1/products/{id}", Json))!;

    public Task<HttpResponseMessage> PutWithETagAsync<T>(string url, T body, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: Json) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return Http.SendAsync(request);
    }

    public async Task<ProductDto> SetStockAsync(string productId, int stock)
    {
        var current = await GetProductAsync(productId);
        var response = await PutWithETagAsync($"/api/v1/products/{productId}/stock", new SetStockRequest { StockQuantity = stock, Reason = "Test setup" }, current.ETag);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDto>(Json))!;
    }

    public async Task<ProductDto> SetPriceAsync(string productId, decimal price)
    {
        var current = await GetProductAsync(productId);
        var response = await PutWithETagAsync($"/api/v1/products/{productId}/price", new ChangePriceRequest { Price = price, Reason = "Test price change" }, current.ETag);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDto>(Json))!;
    }

    public Task<HttpResponseMessage> PutImageAsync(string productId, byte[] content, string fileName, string contentType, string? etag)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/products/{productId}/image")
        {
            Content = new MultipartFormDataContent { { file, "file", fileName } }
        };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return Http.SendAsync(request);
    }

    // ---- cart ----
    public Task<HttpResponseMessage> AddToCartAsync(string productId, int quantity, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items")
        {
            Content = JsonContent.Create(new AddCartItemRequest { ProductId = productId, Quantity = quantity }, options: Json)
        };
        request.Headers.Add(ApiHeaders.IdempotencyKey, key ?? NewKey());
        return Http.SendAsync(request);
    }

    public async Task<CartDto> AddToCartOkAsync(string productId, int quantity)
    {
        var response = await AddToCartAsync(productId, quantity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CartDto>(Json))!;
    }

    public Task<HttpResponseMessage> SendCartAsync(HttpMethod method, string url, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        request.Headers.Add(ApiHeaders.IdempotencyKey, key ?? NewKey());
        return Http.SendAsync(request);
    }

    public async Task<CartDto> GetCartAsync() => (await Http.GetFromJsonAsync<CartDto>("/api/v1/cart", Json))!;

    // ---- checkout / orders ----
    public Task<HttpResponseMessage> CheckoutAsync(Guid orderId, decimal expectedTotal) =>
        Http.PostAsJsonAsync("/api/v1/checkout", new CheckoutRequest { OrderId = orderId, ExpectedTotal = expectedTotal }, Json);

    public async Task<OrderDto> CheckoutOkAsync()
    {
        var cart = await GetCartAsync();
        var response = await CheckoutAsync(Guid.NewGuid(), cart.Total);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>(Json))!;
    }

    public static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await ProblemAsync(response)).TryGetProperty("code", out var c) ? c.GetString() : null;
}
