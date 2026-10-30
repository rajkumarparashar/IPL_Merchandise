using Asp.Versioning;
using IPLMerchandise.API.Security;
using IPLMerchandise.Application.Carts;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPLMerchandise.API.Controllers;

/// <summary>
/// The signed-in customer's cart. Every change needs an Idempotency-Key header; a repeated key returns the original
/// result (with "Idempotent-Replay: true") and changes nothing.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/cart")]
[Authorize(Policy = AuthConstants.CustomerPolicy)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class CartController(CartService carts) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CartDto>> Get(CancellationToken ct) => Ok(await carts.GetAsync(User.GetUserId(), ct));

    [HttpPost("items")]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CartDto>> Add(AddCartItemRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey, CancellationToken ct) =>
        Result(await carts.AddItemAsync(User.GetUserId(), idempotencyKey, request, ct));

    [HttpPut("items/{productId}")]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CartDto>> UpdateQuantity(string productId, UpdateCartItemRequest request,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey, CancellationToken ct) =>
        Result(await carts.UpdateQuantityAsync(User.GetUserId(), idempotencyKey, productId, request, ct));

    [HttpDelete("items/{productId}")]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CartDto>> Remove(string productId,
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey, CancellationToken ct) =>
        Result(await carts.RemoveItemAsync(User.GetUserId(), idempotencyKey, productId, ct));

    /// <summary>Remove every item from the cart.</summary>
    [HttpDelete]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CartDto>> Clear(
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey, CancellationToken ct) =>
        Result(await carts.ClearAsync(User.GetUserId(), idempotencyKey, ct));

    /// <summary>Accept changed prices / reduced stock so checkout can proceed.</summary>
    [HttpPost("accept-changes")]
    [ProducesResponseType<CartDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CartDto>> AcceptChanges(
        [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey, CancellationToken ct) =>
        Result(await carts.AcceptChangesAsync(User.GetUserId(), idempotencyKey, ct));

    private ActionResult<CartDto> Result(CartMutationResult result)
    {
        if (result.Replayed) Response.Headers[ApiHeaders.IdempotentReplay] = "true";
        return Ok(result.Cart);
    }
}
