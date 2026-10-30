using System.Net;
using System.Net.Http.Json;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.IntegrationTests.Infrastructure;

namespace IPLMerchandise.IntegrationTests;

[Collection(ApiCollection.Name)]
public class ProductImageApiTests(ApiFactory factory)
{
    // A real 1×1 PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private static readonly HttpClient Browser = new(); // fetches image URLs the way a browser would: straight from storage

    [Fact]
    public async Task Admin_uploads_a_photo_shoppers_see_it_and_orders_keep_it_after_it_is_removed()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"photo-{Guid.NewGuid():N}", 300m, 10);
        Assert.Null(product.ImageUrl);

        var upload = await admin.PutImageAsync(product.Id, Png, "my holiday.png", "image/png", product.ETag);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var withImage = (await upload.Content.ReadFromJsonAsync<ProductDto>(TestClient.Json))!;
        Assert.NotEqual(product.ETag, withImage.ETag);
        Assert.EndsWith(".png", withImage.ImageUrl);
        Assert.DoesNotContain("holiday", withImage.ImageUrl); // stored under our own name, never the uploader's
        Assert.Equal(withImage.ImageUrl, (await factory.CreateClient().GetFromJsonAsync<ProductDto>($"/api/v1/products/{product.Id}", TestClient.Json))!.ImageUrl);

        var file = await Browser.GetAsync(withImage.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode); // anonymous read, as shoppers load it
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await file.Content.ReadAsByteArrayAsync());

        var customer = await TestClient.CustomerAsync(factory);
        Assert.Equal(withImage.ImageUrl, (await customer.AddToCartOkAsync(product.Id, 1)).Lines.Single().ImageUrl);
        var order = await customer.CheckoutOkAsync();

        var current = await admin.GetProductAsync(product.Id);
        var remove = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/products/{product.Id}/image");
        remove.Headers.TryAddWithoutValidation("If-Match", current.ETag);
        var removed = await admin.Http.SendAsync(remove);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null((await admin.GetProductAsync(product.Id)).ImageUrl);
        var history = (await customer.Http.GetFromJsonAsync<OrderDto>($"/api/v1/orders/{order.Id}", TestClient.Json))!;
        Assert.Equal(withImage.ImageUrl, history.Lines.Single().ImageUrl);
        Assert.Equal(HttpStatusCode.OK, (await Browser.GetAsync(withImage.ImageUrl)).StatusCode);
    }

    [Theory]
    [InlineData("not an image", "photo.png", "image/png")]        // text pretending to be a PNG
    [InlineData("GIF89a......", "photo.gif", "image/gif")]         // a real format we do not accept
    [InlineData("<svg onload=alert(1)>", "photo.svg", "image/svg+xml")] // SVG can carry script
    public async Task Files_that_are_not_jpeg_png_or_webp_are_rejected_whatever_they_are_called(string content, string name, string type)
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"bad-{Guid.NewGuid():N}", 300m, 10);

        var response = await admin.PutImageAsync(product.Id, System.Text.Encoding.UTF8.GetBytes(content), name, type, product.ETag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-image", await TestClient.CodeAsync(response));
        Assert.Null((await admin.GetProductAsync(product.Id)).ImageUrl);
    }

    [Fact]
    public async Task Files_over_2_MB_are_rejected()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"big-{Guid.NewGuid():N}", 300m, 10);
        var big = new byte[ApiLimits.MaxImageBytes + 1];
        Png.CopyTo(big, 0);

        var response = await admin.PutImageAsync(product.Id, big, "big.png", "image/png", product.ETag);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid-image", await TestClient.CodeAsync(response));
    }

    [Fact]
    public async Task Only_admins_with_the_current_etag_can_change_the_photo()
    {
        var admin = await TestClient.AdminAsync(factory);
        var product = await admin.CreateProductAsync($"guard-{Guid.NewGuid():N}", 300m, 10);
        var customer = await TestClient.CustomerAsync(factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PutImageAsync(product.Id, Png, "a.png", "image/png", product.ETag)).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await admin.PutImageAsync(product.Id, Png, "a.png", "image/png", etag: null)).StatusCode);

        await admin.SetPriceAsync(product.Id, 350m); // product.ETag is now stale
        var stale = await admin.PutImageAsync(product.Id, Png, "a.png", "image/png", product.ETag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Null((await admin.GetProductAsync(product.Id)).ImageUrl);
    }
}
