using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Web.Client.Api;

public sealed record ProductQuery(
    string? Search = null,
    IReadOnlyList<ProductType>? Types = null,
    IReadOnlyList<string>? Franchises = null,
    ProductSort Sort = ProductSort.Featured,
    int PageSize = ApiLimits.DefaultPageSize,
    bool IncludeInactive = false,
    bool FeaturedOnly = false);

public interface ICatalogApi
{
    Task<ApiResult<PagedResult<ProductDto>>> SearchAsync(ProductQuery query, string? continuationToken, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> GetAsync(string id, CancellationToken ct = default);
    Task<ApiResult<IReadOnlyList<FranchiseDto>>> GetFranchisesAsync(CancellationToken ct = default);
    Task<ApiResult<ProductFacetsDto>> GetFacetsAsync(string? search, CancellationToken ct = default);
}

public interface ICartApi
{
    Task<ApiResult<CartDto>> GetAsync(CancellationToken ct = default);
    Task<ApiResult<CartDto>> AddAsync(string productId, int quantity, string idempotencyKey, CancellationToken ct = default);
    Task<ApiResult<CartDto>> UpdateAsync(string productId, int quantity, string idempotencyKey, CancellationToken ct = default);
    Task<ApiResult<CartDto>> RemoveAsync(string productId, string idempotencyKey, CancellationToken ct = default);
    Task<ApiResult<CartDto>> AcceptChangesAsync(string idempotencyKey, CancellationToken ct = default);
    Task<ApiResult<CartDto>> ClearAsync(string idempotencyKey, CancellationToken ct = default);
}

public interface IOrdersApi
{
    Task<ApiResult<OrderDto>> CheckoutAsync(Guid orderId, decimal expectedTotal, ShippingAddressDto? address = null, CancellationToken ct = default);
    Task<ApiResult<PagedResult<OrderSummaryDto>>> ListAsync(DateTimeOffset? from, DateTimeOffset? to, string? continuationToken, int pageSize = 10,
        string? orderId = null, OrderStatus? status = null, CancellationToken ct = default);
    Task<ApiResult<OrderStatsDto>> GetStatsAsync(CancellationToken ct = default);
    Task<ApiResult<OrderDto>> GetAsync(Guid id, CancellationToken ct = default);
}

public interface IAuthApi
{
    Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<ApiResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
}

public interface IAdminApi
{
    Task<ApiResult<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> UpdateDetailsAsync(string id, UpdateProductRequest request, string etag, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> ChangePriceAsync(string id, ChangePriceRequest request, string etag, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> SetStockAsync(string id, SetStockRequest request, string etag, CancellationToken ct = default);
    Task<ApiResult<PagedResult<AuditEntryDto>>> GetAuditAsync(string id, string? continuationToken, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> UploadImageAsync(string id, ImageUpload image, string etag, CancellationToken ct = default);
    Task<ApiResult<ProductDto>> RemoveImageAsync(string id, string etag, CancellationToken ct = default);
}

public sealed class CatalogApi(HttpClient http) : ApiClientBase(http), ICatalogApi
{
    public Task<ApiResult<PagedResult<ProductDto>>> SearchAsync(ProductQuery q, string? token, CancellationToken ct = default)
    {
        var query = new List<string> { $"pageSize={q.PageSize}" };
        if (!string.IsNullOrWhiteSpace(q.Search)) query.Add($"search={Uri.EscapeDataString(q.Search.Trim())}");
        foreach (var type in q.Types ?? []) query.Add($"type={type}");
        foreach (var franchise in q.Franchises ?? []) query.Add($"franchise={Uri.EscapeDataString(franchise)}");
        if (q.Sort != ProductSort.Featured) query.Add($"sort={q.Sort}");
        if (q.IncludeInactive) query.Add("includeInactive=true");
        if (q.FeaturedOnly) query.Add("featured=true");
        if (token is not null) query.Add($"continuationToken={Uri.EscapeDataString(token)}");
        return GetAsync<PagedResult<ProductDto>>("api/v1/products?" + string.Join('&', query), ct);
    }

    public Task<ApiResult<ProductDto>> GetAsync(string id, CancellationToken ct = default) =>
        GetAsync<ProductDto>($"api/v1/products/{Uri.EscapeDataString(id)}", ct);

    public Task<ApiResult<IReadOnlyList<FranchiseDto>>> GetFranchisesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FranchiseDto>>("api/v1/franchises", ct);

    public Task<ApiResult<ProductFacetsDto>> GetFacetsAsync(string? search, CancellationToken ct = default) =>
        GetAsync<ProductFacetsDto>("api/v1/products/facets" + (string.IsNullOrWhiteSpace(search) ? "" : $"?search={Uri.EscapeDataString(search.Trim())}"), ct);
}

public sealed class CartApi(HttpClient http) : ApiClientBase(http), ICartApi
{
    public Task<ApiResult<CartDto>> GetAsync(CancellationToken ct = default) => GetAsync<CartDto>("api/v1/cart", ct);

    public Task<ApiResult<CartDto>> AddAsync(string productId, int quantity, string key, CancellationToken ct = default) =>
        SendAsync<CartDto>(Request(HttpMethod.Post, "api/v1/cart/items", new AddCartItemRequest { ProductId = productId, Quantity = quantity }, key), ct);

    public Task<ApiResult<CartDto>> UpdateAsync(string productId, int quantity, string key, CancellationToken ct = default) =>
        SendAsync<CartDto>(Request(HttpMethod.Put, $"api/v1/cart/items/{Uri.EscapeDataString(productId)}", new UpdateCartItemRequest { Quantity = quantity }, key), ct);

    public Task<ApiResult<CartDto>> RemoveAsync(string productId, string key, CancellationToken ct = default) =>
        SendAsync<CartDto>(Request(HttpMethod.Delete, $"api/v1/cart/items/{Uri.EscapeDataString(productId)}", idempotencyKey: key), ct);

    public Task<ApiResult<CartDto>> AcceptChangesAsync(string key, CancellationToken ct = default) =>
        SendAsync<CartDto>(Request(HttpMethod.Post, "api/v1/cart/accept-changes", idempotencyKey: key), ct);

    public Task<ApiResult<CartDto>> ClearAsync(string key, CancellationToken ct = default) =>
        SendAsync<CartDto>(Request(HttpMethod.Delete, "api/v1/cart", idempotencyKey: key), ct);
}

public sealed class OrdersApi(HttpClient http) : ApiClientBase(http), IOrdersApi
{
    public Task<ApiResult<OrderDto>> CheckoutAsync(Guid orderId, decimal expectedTotal, ShippingAddressDto? address = null, CancellationToken ct = default)
    {
        var request = Request(HttpMethod.Post, "api/v1/checkout", new CheckoutRequest
        {
            OrderId = orderId, ExpectedTotal = expectedTotal, ShippingAddress = address, PaymentMethod = PaymentMethod.CashOnDelivery
        });
        request.Options.Set(RetryHandler.SafeToRetry, true); // the order id makes it idempotent
        return SendAsync<OrderDto>(request, ct);
    }

    public Task<ApiResult<PagedResult<OrderSummaryDto>>> ListAsync(DateTimeOffset? from, DateTimeOffset? to, string? token, int pageSize = 10,
        string? orderId = null, OrderStatus? status = null, CancellationToken ct = default)
    {
        var query = new List<string> { $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(orderId)) query.Add($"orderId={Uri.EscapeDataString(orderId.Trim())}");
        if (status is not null) query.Add($"status={status}");
        if (from is not null) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to is not null) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        if (token is not null) query.Add($"continuationToken={Uri.EscapeDataString(token)}");
        return GetAsync<PagedResult<OrderSummaryDto>>("api/v1/orders?" + string.Join('&', query), ct);
    }

    public Task<ApiResult<OrderDto>> GetAsync(Guid id, CancellationToken ct = default) => GetAsync<OrderDto>($"api/v1/orders/{id}", ct);

    public Task<ApiResult<OrderStatsDto>> GetStatsAsync(CancellationToken ct = default) => GetAsync<OrderStatsDto>("api/v1/orders/summary", ct);
}

public sealed class AuthApi(HttpClient http) : ApiClientBase(http), IAuthApi
{
    public Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(Request(HttpMethod.Post, "api/v1/auth/login", request), ct);

    public Task<ApiResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        SendAsync<AuthResponse>(Request(HttpMethod.Post, "api/v1/auth/register", request), ct);
}

public sealed class AdminApi(HttpClient http) : ApiClientBase(http), IAdminApi
{
    public Task<ApiResult<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken ct = default) =>
        SendAsync<ProductDto>(Request(HttpMethod.Post, "api/v1/products", request), ct);

    public Task<ApiResult<ProductDto>> UpdateDetailsAsync(string id, UpdateProductRequest request, string etag, CancellationToken ct = default) =>
        SendAsync<ProductDto>(Request(HttpMethod.Put, $"api/v1/products/{Uri.EscapeDataString(id)}", request, ifMatch: etag), ct);

    public Task<ApiResult<ProductDto>> ChangePriceAsync(string id, ChangePriceRequest request, string etag, CancellationToken ct = default) =>
        SendAsync<ProductDto>(Request(HttpMethod.Put, $"api/v1/products/{Uri.EscapeDataString(id)}/price", request, ifMatch: etag), ct);

    public Task<ApiResult<ProductDto>> SetStockAsync(string id, SetStockRequest request, string etag, CancellationToken ct = default) =>
        SendAsync<ProductDto>(Request(HttpMethod.Put, $"api/v1/products/{Uri.EscapeDataString(id)}/stock", request, ifMatch: etag), ct);

    public Task<ApiResult<ProductDto>> UploadImageAsync(string id, ImageUpload image, string etag, CancellationToken ct = default)
    {
        var file = new ByteArrayContent(image.Content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.ContentType);
        var request = Request(HttpMethod.Put, $"api/v1/products/{Uri.EscapeDataString(id)}/image", ifMatch: etag);
        request.Content = new MultipartFormDataContent { { file, "file", image.FileName } };
        return SendAsync<ProductDto>(request, ct);
    }

    public Task<ApiResult<ProductDto>> RemoveImageAsync(string id, string etag, CancellationToken ct = default) =>
        SendAsync<ProductDto>(Request(HttpMethod.Delete, $"api/v1/products/{Uri.EscapeDataString(id)}/image", ifMatch: etag), ct);

    public Task<ApiResult<PagedResult<AuditEntryDto>>> GetAuditAsync(string id, string? token, CancellationToken ct = default) =>
        GetAsync<PagedResult<AuditEntryDto>>($"api/v1/products/{Uri.EscapeDataString(id)}/audit?pageSize=20"
            + (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}"), ct);
}
