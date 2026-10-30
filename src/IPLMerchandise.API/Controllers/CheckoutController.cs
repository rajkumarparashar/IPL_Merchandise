using Asp.Versioning;
using IPLMerchandise.API.Infrastructure;
using IPLMerchandise.API.Security;
using IPLMerchandise.Application.Checkout;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IPLMerchandise.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/checkout")]
[Authorize(Policy = AuthConstants.CustomerPolicy)]
public sealed class CheckoutController(CheckoutService checkout) : ControllerBase
{
    /// <summary>
    /// Place an order from the cart. 201 for a new order; 200 with the same order when the order ID was already used by
    /// this customer (safe retry). 409 with "failingLines" when stock is short: nothing is deducted and the cart is kept.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Checkout)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrderDto>> PlaceOrder(CheckoutRequest request, CancellationToken ct)
    {
        var result = await checkout.PlaceOrderAsync(User.GetUserId(), request, ct);
        if (!result.Created)
        {
            Response.Headers[ApiHeaders.IdempotentReplay] = "true";
            return Ok(result.Order);
        }
        return CreatedAtAction(nameof(OrdersController.Get), "Orders", new { id = result.Order.Id, version = "1" }, result.Order);
    }
}
