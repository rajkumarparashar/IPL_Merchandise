using Asp.Versioning;
using IPLMerchandise.API.Infrastructure;
using IPLMerchandise.API.Security;
using IPLMerchandise.Application.Catalog;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

namespace IPLMerchandise.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/products")]
public sealed class ProductsController(CatalogService catalog, ProductAdminService admin) : ControllerBase
{
    /// <summary>
    /// List or search products. Paged with an opaque continuation token, stable order, page size capped at 50.
    /// <c>type</c> and <c>franchise</c> may be repeated (OR within each, AND between them); a single value works as before.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Search)]
    [ProducesResponseType<PagedResult<ProductDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<PagedResult<ProductDto>>> Search(
        [FromQuery] string? search, [FromQuery(Name = "type")] ProductType[]? types, [FromQuery(Name = "franchise")] string[]? franchises,
        [FromQuery] ProductSort? sort, [FromQuery] int? pageSize, [FromQuery] string? continuationToken, [FromQuery] bool includeInactive,
        [FromQuery] bool featured, CancellationToken ct)
    {
        // Only admins may see disabled products; for everyone else the flag is ignored.
        var query = new ProductSearchQuery(search, types, franchises, pageSize, continuationToken,
            includeInactive && User.IsInRole(Roles.Admin), sort ?? ProductSort.Featured, featured);
        return Ok(await catalog.SearchAsync(query, ct));
    }

    /// <summary>Number of products per type and franchise for the given search text (for filter counts).</summary>
    [HttpGet("facets")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Search)]
    [ProducesResponseType<ProductFacetsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProductFacetsDto>> Facets([FromQuery] string? search, CancellationToken ct) =>
        Ok(await catalog.GetFacetsAsync(search, ct));

    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductDto>> Get(string id, CancellationToken ct) =>
        WithETag(await catalog.GetAsync(id, includeInactive: User.IsInRole(Roles.Admin), ct));

    [HttpPost]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProductDto>> Create(CreateProductRequest request, CancellationToken ct)
    {
        var product = await admin.CreateAsync(request, User.ToActor(), ct);
        Response.Headers.ETag = product.ETag;
        return CreatedAtAction(nameof(Get), new { id = product.Id, version = "1" }, product);
    }

    /// <summary>Update name, type, franchise, description or availability. Requires If-Match with the current ETag.</summary>
    [HttpPut("{id}")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<ActionResult<ProductDto>> Update(string id, UpdateProductRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        WithETag(await admin.UpdateDetailsAsync(id, request, ifMatch, User.ToActor(), ct));

    /// <summary>Change the price. Audited. Requires If-Match.</summary>
    [HttpPut("{id}/price")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<ProductDto>> ChangePrice(string id, ChangePriceRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        WithETag(await admin.ChangePriceAsync(id, request, ifMatch, User.ToActor(), ct));

    /// <summary>Set the stock level (manual adjustment). Audited. Requires If-Match.</summary>
    [HttpPut("{id}/stock")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<ProductDto>> SetStock(string id, SetStockRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        WithETag(await admin.SetStockAsync(id, request, ifMatch, User.ToActor(), ct));

    // Room for the multipart envelope, so a slightly-too-big file reaches the size check and gets a clear message.
    private const long ImageRequestLimit = ApiLimits.MaxImageBytes + 1024 * 1024;

    /// <summary>
    /// Set or replace the product photo: multipart/form-data with one "file" field, JPEG, PNG or WebP, at most 2 MB.
    /// The format is checked from the file's content. Requires If-Match. Returns the product with its new imageUrl and ETag.
    /// </summary>
    [HttpPut("{id}/image")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ImageRequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = ImageRequestLimit)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ProductDto>> SetImage(string id, IFormFile? file,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            throw RequestValidationException.For("file", "Choose an image file to upload.", ProblemCodes.InvalidImage);

        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream((int)Math.Min(file.Length, ImageRequestLimit));
        await upload.CopyToAsync(buffer, ct);
        return WithETag(await admin.SetImageAsync(id, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ifMatch, User.ToActor(), ct));
    }

    /// <summary>Remove the product photo (shoppers see the illustration again). Requires If-Match.</summary>
    [HttpDelete("{id}/image")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<ProductDto>> RemoveImage(string id, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct) =>
        WithETag(await admin.RemoveImageAsync(id, ifMatch, User.ToActor(), ct));

    /// <summary>Price and stock audit trail, newest first.</summary>
    [HttpGet("{id}/audit")]
    [Authorize(Policy = AuthConstants.AdminPolicy)]
    [ProducesResponseType<PagedResult<AuditEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<AuditEntryDto>>> Audit(string id, [FromQuery] int? pageSize,
        [FromQuery] string? continuationToken, CancellationToken ct) =>
        Ok(await admin.GetAuditAsync(id, pageSize, continuationToken, ct));

    private OkObjectResult WithETag(ProductDto product)
    {
        Response.Headers[HeaderNames.ETag] = product.ETag;
        return Ok(product);
    }
}
