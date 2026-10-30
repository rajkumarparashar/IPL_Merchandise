using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Web.Client.Api;

namespace IPLMerchandise.Web.Tests.Fakes;

/// <summary>Lets a test hold a call "in flight" to observe loading states and double-clicks.</summary>
public sealed class Gate
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Closed { get; private set; }
    public void Close() { Closed = true; _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Open() { Closed = false; _tcs.TrySetResult(); }
    public Task WaitAsync() => Closed ? _tcs.Task : Task.CompletedTask;
}

public static class Samples
{
    public static ProductDto Product(string id, string name, decimal price, int stock, ProductType type = ProductType.Jersey, string franchise = "MI") => new()
    {
        Id = id, Name = name, Type = type, FranchiseCode = franchise, FranchiseName = franchise + " team", Description = "desc",
        Price = price, StockQuantity = stock,
        StockStatus = stock == 0 ? StockStatus.OutOfStock : stock <= 5 ? StockStatus.LowStock : StockStatus.InStock,
        IsLimitedEdition = type == ProductType.SignedPhotograph, IsActive = true, UpdatedAtUtc = DateTime.UtcNow,
        ETag = "\"etag-1\"", MaxQuantityPerLine = type == ProductType.SignedPhotograph ? 2 : 10, IsFeatured = true
    };

    public static readonly ProductDto MiJersey = Product("mi-jersey", "MI Jersey", 1999.00m, 50);
    public static readonly ProductDto CskCap = Product("csk-cap", "CSK Cap", 499.00m, 100, ProductType.Cap, "CSK");
    public static readonly ProductDto RcbFlag = Product("rcb-flag", "RCB Flag", 349.00m, 200, ProductType.Flag, "RCB");
    public static readonly ProductDto KkrPhoto = Product("kkr-photo", "KKR Signed Photo", 4999.00m, 2, ProductType.SignedPhotograph, "KKR");

    public static CartDto Cart(params CartLineDto[] lines)
    {
        var subtotal = lines.Sum(l => l.LineTotal);
        var after = lines.Sum(l => (l.CurrentUnitPrice ?? 0) * l.Quantity);
        return new CartDto
        {
            Lines = lines, ItemCount = lines.Sum(l => l.Quantity), Subtotal = subtotal, ShippingFee = 0, Total = subtotal,
            RequiresReview = lines.Any(l => l.Issues.Count > 0), TotalAfterAccept = after
        };
    }

    public static CartLineDto Line(ProductDto p, int qty, decimal? currentPrice = null, params CartLineIssue[] issues) => new()
    {
        ProductId = p.Id, ProductName = p.Name, FranchiseCode = p.FranchiseCode, UnitPrice = p.Price,
        CurrentUnitPrice = currentPrice ?? p.Price, Quantity = qty, AvailableStock = p.StockQuantity, MaxQuantity = 10,
        LineTotal = p.Price * qty, Issues = issues
    };

    public static ApiProblem Problem(int status, string code, string detail = "Rejected") =>
        new() { Status = status, Code = code, Title = "Problem", Detail = detail, CorrelationId = "corr-123" };
}

public sealed class FakeCatalogApi : ICatalogApi
{
    public List<ProductDto> Products { get; } = [Samples.MiJersey, Samples.CskCap, Samples.RcbFlag, Samples.KkrPhoto];
    public List<(ProductQuery Query, string? Token)> Searches { get; } = [];
    public Queue<ApiProblem> FailNext { get; } = new();
    public Gate Gate { get; } = new();

    public async Task<ApiResult<PagedResult<ProductDto>>> SearchAsync(ProductQuery query, string? token, CancellationToken ct = default)
    {
        Searches.Add((query, token));
        await Gate.WaitAsync();
        if (FailNext.TryDequeue(out var problem)) return ApiResult<PagedResult<ProductDto>>.Fail(problem);

        var filtered = Products
            .Where(p => string.IsNullOrEmpty(query.Search) || p.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(p => query.Types is not { Count: > 0 } || query.Types.Contains(p.Type))
            .Where(p => query.Franchises is not { Count: > 0 } || query.Franchises.Contains(p.FranchiseCode))
            .Where(p => !query.FeaturedOnly || p.IsFeatured == true).ToList();
        var skip = token is null ? 0 : int.Parse(token);
        var items = filtered.Skip(skip).Take(query.PageSize).ToList();
        var next = skip + items.Count < filtered.Count ? (skip + items.Count).ToString() : null;
        return ApiResult<PagedResult<ProductDto>>.Ok(new PagedResult<ProductDto>(items, query.PageSize, next, token is null ? filtered.Count : null));
    }

    public Task<ApiResult<ProductDto>> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Products.FirstOrDefault(p => p.Id == id) is { } p
            ? ApiResult<ProductDto>.Ok(p, etag: p.ETag)
            : ApiResult<ProductDto>.Fail(Samples.Problem(404, "not-found")));

    public Task<ApiResult<IReadOnlyList<FranchiseDto>>> GetFranchisesAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<IReadOnlyList<FranchiseDto>>.Ok([new("CSK", "Chennai Super Kings"), new("MI", "Mumbai Indians")]));

    public Task<ApiResult<ProductFacetsDto>> GetFacetsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<ProductFacetsDto>.Ok(new ProductFacetsDto(
            Enum.GetValues<ProductType>().Select(t => new TypeFacet(t, Products.Count(p => p.Type == t))).ToList(),
            Products.GroupBy(p => p.FranchiseCode).Select(g => new FranchiseFacet(g.Key, g.First().FranchiseName, g.Count())).ToList())));
}

public sealed class FakeCartApi : ICartApi
{
    public CartDto Cart { get; set; } = Samples.Cart();
    public List<(string Operation, string Key)> Calls { get; } = [];
    public Queue<ApiProblem> FailNext { get; } = new();
    public Gate Gate { get; } = new();

    public int Gets { get; private set; }

    public Task<ApiResult<CartDto>> GetAsync(CancellationToken ct = default)
    {
        Gets++;
        return Task.FromResult(ApiResult<CartDto>.Ok(Cart));
    }

    public Task<ApiResult<CartDto>> AddAsync(string productId, int quantity, string key, CancellationToken ct = default) => Record($"add:{productId}:{quantity}", key);
    public Task<ApiResult<CartDto>> UpdateAsync(string productId, int quantity, string key, CancellationToken ct = default) => Record($"update:{productId}:{quantity}", key);
    public Task<ApiResult<CartDto>> RemoveAsync(string productId, string key, CancellationToken ct = default) => Record($"remove:{productId}", key);
    public Task<ApiResult<CartDto>> AcceptChangesAsync(string key, CancellationToken ct = default) => Record("accept", key);
    public Task<ApiResult<CartDto>> ClearAsync(string key, CancellationToken ct = default) => Record("clear", key);

    /// <summary>What the cart looks like after the next successful mutation.</summary>
    public CartDto? NextCart { get; set; }

    private async Task<ApiResult<CartDto>> Record(string operation, string key)
    {
        Calls.Add((operation, key));
        await Gate.WaitAsync();
        if (FailNext.TryDequeue(out var problem)) return ApiResult<CartDto>.Fail(problem);
        if (NextCart is not null) Cart = NextCart;
        return ApiResult<CartDto>.Ok(Cart);
    }
}

public sealed class FakeOrdersApi : IOrdersApi
{
    public List<(Guid OrderId, decimal Total)> Checkouts { get; } = [];
    public Queue<ApiProblem> FailNext { get; } = new();
    public Gate Gate { get; } = new();
    public List<OrderSummaryDto> History { get; } = [];
    public List<(DateTimeOffset? From, DateTimeOffset? To)> ListCalls { get; } = [];

    public List<ShippingAddressDto?> Addresses { get; } = [];

    public async Task<ApiResult<OrderDto>> CheckoutAsync(Guid orderId, decimal expectedTotal, ShippingAddressDto? address = null, CancellationToken ct = default)
    {
        Checkouts.Add((orderId, expectedTotal));
        Addresses.Add(address);
        await Gate.WaitAsync();
        if (FailNext.TryDequeue(out var problem)) return ApiResult<OrderDto>.Fail(problem);
        return ApiResult<OrderDto>.Ok(new OrderDto(orderId, DateTime.UtcNow, OrderStatus.Confirmed, [], expectedTotal, 0, expectedTotal), 201);
    }

    public Task<ApiResult<OrderStatsDto>> GetStatsAsync(CancellationToken ct = default) =>
        Task.FromResult(ApiResult<OrderStatsDto>.Ok(new OrderStatsDto(History.Count, History.Sum(o => o.Total), History.Sum(o => o.Total))));

    public Task<ApiResult<PagedResult<OrderSummaryDto>>> ListAsync(DateTimeOffset? from, DateTimeOffset? to, string? token, int pageSize = 10,
        string? orderId = null, OrderStatus? status = null, CancellationToken ct = default)
    {
        ListCalls.Add((from, to));
        return Task.FromResult(ApiResult<PagedResult<OrderSummaryDto>>.Ok(new PagedResult<OrderSummaryDto>(History, pageSize, null)));
    }

    public Task<ApiResult<OrderDto>> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<OrderDto>.Ok(new OrderDto(id, new DateTime(2026, 4, 1, 18, 30, 0, DateTimeKind.Utc), OrderStatus.Confirmed,
            [new OrderLineDto("csk-cap", "CSK Cap", "CSK", 499m, 3, 1497m)], 1497m, 0, 1497m)));
}

public sealed class FakeAuthApi : IAuthApi
{
    public int Calls { get; private set; }
    public ApiProblem? Fail { get; set; }

    public Task<ApiResult<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default) => Respond();
    public Task<ApiResult<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct = default) => Respond();

    private Task<ApiResult<AuthResponse>> Respond()
    {
        Calls++;
        return Task.FromResult(Fail is not null
            ? ApiResult<AuthResponse>.Fail(Fail)
            : ApiResult<AuthResponse>.Ok(new AuthResponse("x.y.z", DateTime.UtcNow.AddHours(1), new UserDto("u1", "a@x.com", "Asha", ["Customer"]))));
    }
}

public sealed class FakeAdminApi : IAdminApi
{
    public ApiProblem? Fail { get; set; }
    public List<string> ETagsSent { get; } = [];

    public Task<ApiResult<ProductDto>> CreateAsync(CreateProductRequest request, CancellationToken ct = default) => Respond("");
    public Task<ApiResult<ProductDto>> UpdateDetailsAsync(string id, UpdateProductRequest r, string etag, CancellationToken ct = default) => Respond(etag);
    public Task<ApiResult<ProductDto>> ChangePriceAsync(string id, ChangePriceRequest r, string etag, CancellationToken ct = default) =>
        Respond(etag, Samples.CskCap with { Price = r.Price!.Value, ETag = "\"etag-2\"" });
    public Task<ApiResult<ProductDto>> SetStockAsync(string id, SetStockRequest r, string etag, CancellationToken ct = default) => Respond(etag);
    public List<AuditEntryDto> AuditEntries { get; } = [];

    public Task<ApiResult<PagedResult<AuditEntryDto>>> GetAuditAsync(string id, string? token, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<PagedResult<AuditEntryDto>>.Ok(new PagedResult<AuditEntryDto>(AuditEntries, 20, null)));

    public List<(string Id, string FileName, string ETag)> Uploads { get; } = [];
    public List<(string Id, string ETag)> Removals { get; } = [];
    public ApiProblem? FailUpload { get; set; }

    public Task<ApiResult<ProductDto>> UploadImageAsync(string id, ImageUpload image, string etag, CancellationToken ct = default)
    {
        Uploads.Add((id, image.FileName, etag));
        if (FailUpload is not null) return Task.FromResult(ApiResult<ProductDto>.Fail(FailUpload));
        return Respond(etag, Samples.CskCap with { ImageUrl = "https://images.test/csk-cap/new.png", ETag = "\"etag-3\"" });
    }

    public Task<ApiResult<ProductDto>> RemoveImageAsync(string id, string etag, CancellationToken ct = default)
    {
        Removals.Add((id, etag));
        return Respond(etag, Samples.CskCap with { ImageUrl = null, ETag = "\"etag-3\"" });
    }

    private Task<ApiResult<ProductDto>> Respond(string etag, ProductDto? result = null)
    {
        ETagsSent.Add(etag);
        return Task.FromResult(Fail is not null ? ApiResult<ProductDto>.Fail(Fail) : ApiResult<ProductDto>.Ok(result ?? Samples.CskCap));
    }
}

/// <summary>Stands in for the SignalR connection: records what is watched and lets a test push a change.</summary>
public sealed class FakeProductUpdates : IPLMerchandise.Web.Client.State.IProductUpdates
{
    public List<string[]> Watched { get; } = [];
    public event Action<IReadOnlyList<string>>? ProductsChanged;
    public event Action? Reconnected;

    public Task WatchAsync(IReadOnlyCollection<string> productIds)
    {
        Watched.Add(productIds.ToArray());
        return Task.CompletedTask;
    }

    public void Push(params string[] productIds) => ProductsChanged?.Invoke(productIds);
    public void Reconnect() => Reconnected?.Invoke();
}
