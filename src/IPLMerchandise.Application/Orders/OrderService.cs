using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;

namespace IPLMerchandise.Application.Orders;

public sealed partial class OrderService(IOrderRepository orders, TimeProvider clock)
{
    /// <summary>
    /// Newest first. The range is half-open: fromUtc &lt;= createdAt &lt; toUtc. <paramref name="orderId"/> matches the start of
    /// the order id (hex digits and dashes; the "#IPL-" display prefix is ignored).
    /// </summary>
    public async Task<PagedResult<OrderSummaryDto>> ListAsync(string customerId, DateTimeOffset? from, DateTimeOffset? to,
        int? pageSize, string? token, CancellationToken ct, string? orderId = null, Contracts.V1.OrderStatus? status = null)
    {
        var size = Paging.NormalisePageSize(pageSize);
        var fromUtc = from?.UtcDateTime;
        var toUtc = to?.UtcDateTime;
        if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            throw RequestValidationException.For("from", "'from' must be earlier than 'to'.");

        var prefix = NormaliseOrderId(orderId);
        var filter = new OrderListFilter(fromUtc, toUtc, prefix, status is null ? null : Mapping.MapEnum<Domain.Orders.OrderStatus>(status.Value));

        // The customer id is part of the fingerprint: a token from one account is useless on another.
        var fingerprint = ContinuationToken.Fingerprint("orders", customerId, fromUtc?.Ticks, toUtc?.Ticks, prefix, filter.Status);
        var after = ContinuationToken.Decode<OrderCursor>(token, fingerprint);

        var page = await orders.ListAsync(customerId, filter, after, size, ct);
        var last = page.Items.LastOrDefault();
        var next = page.HasMore && last is not null
            ? ContinuationToken.Encode(new OrderCursor(last.CreatedAtUtc, last.Id.ToString("D")), fingerprint)
            : null;
        return new PagedResult<OrderSummaryDto>(page.Items.Select(o => o.ToDto()).ToList(), size, next);
    }

    /// <summary>Order count, total spent and spent in the last 30 days (calendar-independent: now minus 30 days).</summary>
    public async Task<OrderStatsDto> GetStatsAsync(string customerId, CancellationToken ct)
    {
        var totals = await orders.GetTotalsAsync(customerId, ct);
        var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
        return new OrderStatsDto(totals.Count, totals.Where(t => t.CreatedAtUtc >= since).Sum(t => t.Total), totals.Sum(t => t.Total));
    }

    private static string? NormaliseOrderId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim().TrimStart('#');
        if (text.StartsWith("IPL", StringComparison.OrdinalIgnoreCase)) text = text[3..].TrimStart('-');
        if (!OrderIdPattern().IsMatch(text))
            throw RequestValidationException.For("orderId", "Enter the order number shown on your order, e.g. 1A2B3C4D.");
        return text.ToLowerInvariant();
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[0-9A-Fa-f-]{1,36}$")]
    private static partial System.Text.RegularExpressions.Regex OrderIdPattern();

    /// <summary>Looks only in the caller's partition, so another customer's order is indistinguishable from a missing one (404).</summary>
    public async Task<OrderDto> GetAsync(string customerId, Guid orderId, CancellationToken ct)
    {
        var order = await orders.GetAsync(customerId, orderId, ct) ?? throw new NotFoundException("Order", orderId.ToString("D"));
        return order.ToDto();
    }
}
