using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Catalog;

namespace IPLMerchandise.Application.Catalog;

public sealed record ProductSearchQuery(
    string? Search,
    IReadOnlyList<Contracts.V1.ProductType>? Types,
    IReadOnlyList<string>? Franchises,
    int? PageSize,
    string? ContinuationToken,
    bool IncludeInactive = false,
    ProductSort Sort = ProductSort.Featured,
    bool FeaturedOnly = false);

/// <summary>Read side of the catalogue: listing, search, facets and product details.</summary>
public sealed class CatalogService(IProductRepository products)
{
    public async Task<PagedResult<ProductDto>> SearchAsync(ProductSearchQuery query, CancellationToken ct)
    {
        var pageSize = Paging.NormalisePageSize(query.PageSize);
        var criteria = ToCriteria(query);
        var sort = MapSort(query.Sort);
        var fingerprint = ContinuationToken.Fingerprint("products", criteria.NameContains, string.Join(',', criteria.Types),
            string.Join(',', criteria.FranchiseCodes), criteria.IncludeInactive, sort, criteria.FeaturedOnly);
        var after = ContinuationToken.Decode<ProductCursor>(query.ContinuationToken, fingerprint);

        var page = await products.SearchAsync(criteria, sort, after, pageSize, ct);

        // Counting costs RUs, so only the first page pays for it; clients keep the number while paging.
        long? total = after is null ? await products.CountAsync(criteria, ct) : null;

        var last = page.Items.LastOrDefault();
        var next = page.HasMore && last is not null
            ? ContinuationToken.Encode(new ProductCursor(last.Id, last.CreatedAtUtc, last.Price, last.Name.ToLowerInvariant(), last.IsFeatured), fingerprint)
            : null;

        return new PagedResult<ProductDto>(page.Items.Select(p => p.ToDto()).ToList(), pageSize, next, total);
    }

    /// <summary>
    /// Counts per type and franchise for the current search text (other filters are not applied, so a shopper can
    /// see what each additional choice would add). Every type and franchise is returned, including zero counts.
    /// </summary>
    public async Task<ProductFacetsDto> GetFacetsAsync(string? search, CancellationToken ct)
    {
        var name = NormaliseSearch(search, out var error);
        if (error is not null) throw RequestValidationException.For("search", error);

        var keys = await products.GetFacetKeysAsync(name, ct);
        return new ProductFacetsDto(
            Enum.GetValues<Domain.Catalog.ProductType>()
                .Select(t => new TypeFacet(Mapping.MapEnum<Contracts.V1.ProductType>(t), keys.Count(k => k.Type == t))).ToList(),
            Franchises.All
                .Select(f => new FranchiseFacet(f.Code, f.Name, keys.Count(k => k.FranchiseCode == f.Code))).ToList());
    }

    /// <summary>Inactive products are hidden from shoppers (404) but visible to admins.</summary>
    public async Task<ProductDto> GetAsync(string id, bool includeInactive, CancellationToken ct)
    {
        var product = await products.GetAsync(id, ct);
        if (product is null || (!product.IsActive && !includeInactive))
            throw new NotFoundException("Product", id);
        return product.ToDto();
    }

    public static IReadOnlyList<FranchiseDto> GetFranchises() =>
        Franchises.All.Select(f => new FranchiseDto(f.Code, f.Name)).ToList();

    private static ProductSortOrder MapSort(ProductSort sort) => sort switch
    {
        ProductSort.PriceLowToHigh => ProductSortOrder.PriceAscending,
        ProductSort.PriceHighToLow => ProductSortOrder.PriceDescending,
        ProductSort.NameAToZ => ProductSortOrder.Name,
        _ => ProductSortOrder.Featured
    };

    private static string? NormaliseSearch(string? search, out string? error)
    {
        var trimmed = search?.Trim();
        error = trimmed?.Length > ApiLimits.MaxSearchLength ? $"Search text can be at most {ApiLimits.MaxSearchLength} characters." : null;
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static ProductSearchCriteria ToCriteria(ProductSearchQuery q)
    {
        var errors = new Dictionary<string, string[]>();
        var name = NormaliseSearch(q.Search, out var searchError);
        if (searchError is not null) errors["search"] = [searchError];

        var franchises = new List<string>();
        foreach (var code in (q.Franchises ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Franchises.TryGet(code.Trim(), out var f)) franchises.Add(f.Code);
            else errors["franchise"] = [$"Unknown franchise '{code}'."];
        }
        if (errors.Count > 0) throw new RequestValidationException(errors);

        var types = (q.Types ?? []).Distinct().Select(t => Mapping.MapEnum<Domain.Catalog.ProductType>(t)).OrderBy(t => t).ToList();
        return new ProductSearchCriteria(name, types, franchises.Order().ToList(), q.IncludeInactive, q.FeaturedOnly);
    }
}
