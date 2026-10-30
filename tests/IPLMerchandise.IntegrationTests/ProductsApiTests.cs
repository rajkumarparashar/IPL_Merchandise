using System.Net;
using System.Net.Http.Json;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Infrastructure.Cosmos;
using IPLMerchandise.IntegrationTests.Infrastructure;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ProductsApiTests(ApiFactory factory)
{
    [Fact]
    public async Task Paging_a_large_list_returns_every_item_once_in_stable_order_with_a_capped_page_size()
    {
        var admin = await TestClient.AdminAsync(factory);
        var tag = $"pg{Guid.NewGuid():N}"[..10];
        var created = new List<string>();
        for (var i = 0; i < 7; i++) created.Add((await admin.CreateProductAsync($"{tag} item {i}", 100 + i, 5)).Id);

        var anon = factory.CreateClient();
        var seen = new List<string>();
        string? token = null;
        do
        {
            var url = $"/api/v1/products?search={tag}&pageSize=3" + (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var page = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>(url, TestClient.Json))!;
            seen.AddRange(page.Items.Select(p => p.Id));
            token = page.ContinuationToken;
        } while (token is not null);

        Assert.Equal(seen.Count, seen.Distinct().Count()); // nothing repeated
        Assert.Equal(created.Order(), seen.Order());        // nothing missing

        var huge = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?pageSize=1000", TestClient.Json))!;
        Assert.Equal(ApiLimits.MaxPageSize, huge.PageSize);
    }

    [Fact]
    public async Task Search_combines_name_type_and_franchise()
    {
        var anon = factory.CreateClient();

        var capCsk = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?type=Cap&franchise=CSK", TestClient.Json))!;
        var cap = Assert.Single(capCsk.Items);
        Assert.Equal(("CSK Cap", 499.00m, StockStatus.InStock), (cap.Name, cap.Price, cap.StockStatus));

        var partial = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>("/api/v1/products?search=sIgNeD", TestClient.Json))!;
        Assert.Contains(partial.Items, p => p.Name == "KKR Signed Photo");
    }

    [Fact]
    public async Task Sorting_multi_value_filters_and_facet_counts()
    {
        var admin = await TestClient.AdminAsync(factory);
        var tag = $"st{Guid.NewGuid():N}"[..10];
        await admin.CreateProductAsync($"{tag} b-flag", 300m, 5, ProductType.Flag, "SRH");
        await admin.CreateProductAsync($"{tag} a-cap", 100m, 5, ProductType.Cap, "SRH");
        await admin.CreateProductAsync($"{tag} c-jersey", 200m, 5, ProductType.Jersey, "DC");
        var anon = factory.CreateClient();

        async Task<List<string>> Names(string query)
        {
            var names = new List<string>();
            string? token = null;
            do // page size 2 so the keyset cursor is exercised for each sort order
            {
                var url = $"/api/v1/products?search={tag}&pageSize=2&{query}" + (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
                var page = (await anon.GetFromJsonAsync<PagedResult<ProductDto>>(url, TestClient.Json))!;
                names.AddRange(page.Items.Select(p => p.Name[(tag.Length + 1)..]));
                token = page.ContinuationToken;
            } while (token is not null);
            return names;
        }

        Assert.Equal(["a-cap", "c-jersey", "b-flag"], await Names("sort=PriceLowToHigh"));
        Assert.Equal(["b-flag", "c-jersey", "a-cap"], await Names("sort=PriceHighToLow"));
        Assert.Equal(["a-cap", "b-flag", "c-jersey"], await Names("sort=NameAToZ"));
        Assert.Equal(["a-cap", "b-flag"], await Names("sort=NameAToZ&type=Cap&type=Flag"));        // OR within a filter
        Assert.Equal(["c-jersey"], await Names("type=Jersey&type=Cap&franchise=DC"));               // AND between filters

        var facets = (await anon.GetFromJsonAsync<ProductFacetsDto>($"/api/v1/products/facets?search={tag}", TestClient.Json))!;
        Assert.Equal(1, facets.Types.Single(t => t.Type == ProductType.Cap).Count);
        Assert.Equal(0, facets.Types.Single(t => t.Type == ProductType.SignedPhotograph).Count);
        Assert.Equal(2, facets.Franchises.Single(f => f.Code == "SRH").Count);
    }

    [Fact]
    public async Task Featured_filter_returns_only_featured_products_and_edits_keep_the_flag()
    {
        var admin = await TestClient.AdminAsync(factory);
        var tag = $"ft{Guid.NewGuid():N}"[..10];
        var featured = (await (await admin.Http.PostAsJsonAsync("/api/v1/products", new CreateProductRequest
        {
            Name = $"{tag} featured", Type = ProductType.Cap, FranchiseCode = "MI", Price = 100m, StockQuantity = 5, IsFeatured = true
        }, TestClient.Json)).Content.ReadFromJsonAsync<ProductDto>(TestClient.Json))!;
        await admin.CreateProductAsync($"{tag} plain", 100m, 5);

        // An admin edit that does not mention the flag (older client) must not clear it.
        var edit = await admin.PutWithETagAsync($"/api/v1/products/{featured.Id}", new UpdateProductRequest
        {
            Name = featured.Name, Type = featured.Type, FranchiseCode = "MI", Description = "updated", IsActive = true
        }, featured.ETag);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var page = (await factory.CreateClient().GetFromJsonAsync<PagedResult<ProductDto>>(
            $"/api/v1/products?search={tag}&featured=true", TestClient.Json))!;
        var only = Assert.Single(page.Items);
        Assert.Equal((featured.Id, true), (only.Id, only.IsFeatured));
    }

    [Fact]
    public async Task Featured_order_lists_featured_products_first_across_page_boundaries()
    {
        var admin = await TestClient.AdminAsync(factory);
        var tag = $"fo{Guid.NewGuid():N}"[..10];
        async Task Create(string suffix, bool featured) =>
            (await admin.Http.PostAsJsonAsync("/api/v1/products", new CreateProductRequest
            {
                Name = $"{tag} {suffix}", Type = ProductType.Flag, FranchiseCode = "MI", Price = 10m, StockQuantity = 5, IsFeatured = featured
            }, TestClient.Json)).EnsureSuccessStatusCode();
        await Create("a", false);
        await Create("b", true);
        await Create("c", false);
        await Create("d", true);

        // A product stored before the isFeatured field existed must still be listed (in the non-featured group).
        var cosmos = factory.Services.GetRequiredService<CosmosContainers>();
        await cosmos.Catalog.CreateItemAsync(new
        {
            id = $"{tag}-legacy", storeId = cosmos.StoreId, docType = "product", name = $"{tag} e", searchName = $"{tag} e",
            type = "Flag", franchiseCode = "MI", description = "", price = 10m, stockQuantity = 5, isActive = true,
            createdAtUtc = CosmosJson.Format(DateTime.UtcNow.AddMinutes(1)), updatedAtUtc = CosmosJson.Format(DateTime.UtcNow)
        }, new PartitionKey(cosmos.StoreId));

        async Task<List<List<string>>> Pages(int size)
        {
            var pages = new List<List<string>>();
            string? token = null;
            do
            {
                var url = $"/api/v1/products?search={tag}&pageSize={size}" + (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
                var page = (await factory.CreateClient().GetFromJsonAsync<PagedResult<ProductDto>>(url, TestClient.Json))!;
                pages.Add(page.Items.Select(p => p.Name[(tag.Length + 1)..]).ToList());
                token = page.ContinuationToken;
            } while (token is not null);
            return pages;
        }

        Assert.Equal([["b", "d"], ["a", "c"], ["e"]], await Pages(2));       // featured first, then the rest, each oldest first
        Assert.Equal([["b", "d", "a"], ["c", "e"]], await Pages(3));         // a page spanning both groups
    }

    [Theory]
    [InlineData("/api/v1/products?pageSize=0", "pageSize")]
    [InlineData("/api/v1/products?type=Mug", "type")]
    [InlineData("/api/v1/products?continuationToken=garbage", "continuationToken")]
    public async Task Invalid_input_returns_400_problem_details_naming_the_field(string url, string field)
    {
        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await TestClient.ProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task Unknown_product_is_404_problem_details()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/products/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not-found", await TestClient.CodeAsync(response));
    }

    [Fact]
    public async Task Product_response_has_etag_version_header_and_the_backwards_compatible_field()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/products/kkr-signed-photo");
        var product = (await response.Content.ReadFromJsonAsync<ProductDto>(TestClient.Json))!;

        Assert.Equal(product.ETag, response.Headers.ETag?.ToString());
        Assert.Equal(2, product.MaxQuantityPerLine); // added later in v1, optional
        Assert.Equal("1.0", response.Headers.GetValues("api-supported-versions").Single());
    }

    /// <summary>
    /// The guard for existing v1 clients (DESIGN ADR-8): every field of the original product contract is still sent with
    /// the same name and JSON type. Later fields are only ever added; renaming, retyping or removing one fails here.
    /// </summary>
    [Fact]
    public async Task Original_v1_product_fields_keep_their_names_and_types()
    {
        var json = await factory.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/products/csk-cap");

        var original = new Dictionary<string, System.Text.Json.JsonValueKind>
        {
            ["id"] = System.Text.Json.JsonValueKind.String, ["name"] = System.Text.Json.JsonValueKind.String,
            ["type"] = System.Text.Json.JsonValueKind.String, ["franchiseCode"] = System.Text.Json.JsonValueKind.String,
            ["franchiseName"] = System.Text.Json.JsonValueKind.String, ["description"] = System.Text.Json.JsonValueKind.String,
            ["price"] = System.Text.Json.JsonValueKind.Number, ["stockQuantity"] = System.Text.Json.JsonValueKind.Number,
            ["stockStatus"] = System.Text.Json.JsonValueKind.String, ["isLimitedEdition"] = System.Text.Json.JsonValueKind.False,
            ["isActive"] = System.Text.Json.JsonValueKind.True, ["updatedAtUtc"] = System.Text.Json.JsonValueKind.String,
            ["eTag"] = System.Text.Json.JsonValueKind.String
        };
        foreach (var (field, kind) in original)
        {
            Assert.True(json.TryGetProperty(field, out var value), $"v1 field '{field}' is missing");
            Assert.Equal(kind, value.ValueKind);
        }
        Assert.Equal("Cap", json.GetProperty("type").GetString()); // enums stay strings, not numbers
    }
}
