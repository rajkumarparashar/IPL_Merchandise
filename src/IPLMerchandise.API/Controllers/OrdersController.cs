using Asp.Versioning;
using IPLMerchandise.API.Security;
using IPLMerchandise.Application.Orders;
using IPLMerchandise.Contracts.V1;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPLMerchandise.API.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/orders")]
[Authorize(Policy = AuthConstants.CustomerPolicy)]
public sealed class OrdersController(OrderService orders) : ControllerBase
{
    /// <summary>The caller's orders, newest first. Optional range: from (inclusive) to (exclusive), ISO-8601 with offset.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<OrderSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<OrderSummaryDto>>> List([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int? pageSize, [FromQuery] string? continuationToken, [FromQuery] string? orderId, [FromQuery] OrderStatus? status,
        CancellationToken ct) =>
        Ok(await orders.ListAsync(User.GetUserId(), from, to, pageSize, continuationToken, ct, orderId, status));

    /// <summary>Headline numbers for the caller's orders: count, total spent, spent in the last 30 days.</summary>
    [HttpGet("summary")]
    [ProducesResponseType<OrderStatsDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<OrderStatsDto>> Summary(CancellationToken ct) =>
        Ok(await orders.GetStatsAsync(User.GetUserId(), ct));

    /// <summary>One of the caller's orders. Another customer's order returns 404, not 403, to avoid confirming it exists.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await orders.GetAsync(User.GetUserId(), id, ct));
}
