using System.Net;
using System.Text;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Orders;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;

namespace IPLMerchandise.Infrastructure.Repositories;

public sealed class CosmosOrderRepository(CosmosContainers cosmos) : IOrderRepository
{
    private Container Container => cosmos.Orders;

    public async Task<Order?> GetAsync(string customerId, Guid orderId, CancellationToken ct)
    {
        var doc = await CosmosQuery.ReadOrDefaultAsync<OrderDocument>(Container, orderId.ToString("D"), customerId, ct);
        return doc?.ToDomain();
    }

    public async Task<Order> CreateIfNotExistsAsync(Order order, CancellationToken ct)
    {
        try
        {
            await Container.CreateItemAsync(OrderDocument.From(order), new PartitionKey(order.CustomerId),
                new ItemRequestOptions { EnableContentResponseOnWrite = false }, ct);
            return order;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return await GetAsync(order.CustomerId, order.Id, ct) ?? order;
        }
    }

    public async Task<Page<OrderSummary>> ListAsync(string customerId, OrderListFilter filter, OrderCursor? after, int pageSize, CancellationToken ct)
    {
        // Projection: the list needs summary fields plus the (small) line snapshots for thumbnails; addresses stay out.
        var sql = new StringBuilder(
            $"SELECT TOP {pageSize + 1} c.id, c.createdAtUtc, c.status, c.itemCount, c.total, c.lines FROM c WHERE c.customerId = @customerId AND c.docType = @docType");
        var query = new Dictionary<string, object> { ["@customerId"] = customerId, ["@docType"] = DocTypes.Order };

        if (filter.FromUtc is not null) { sql.Append(" AND c.createdAtUtc >= @from"); query["@from"] = CosmosJson.Format(filter.FromUtc.Value); }
        if (filter.ToUtc is not null) { sql.Append(" AND c.createdAtUtc < @to"); query["@to"] = CosmosJson.Format(filter.ToUtc.Value); }
        if (filter.IdPrefix is not null) { sql.Append(" AND STARTSWITH(c.id, @idPrefix)"); query["@idPrefix"] = filter.IdPrefix; }
        if (filter.Status is not null) { sql.Append(" AND c.status = @status"); query["@status"] = filter.Status.Value.ToString(); }
        if (after is not null)
        {
            sql.Append(" AND (c.createdAtUtc < @afterAt OR (c.createdAtUtc = @afterAt AND c.id < @afterId))");
            query["@afterAt"] = CosmosJson.Format(after.CreatedAtUtc);
            query["@afterId"] = after.Id;
        }
        sql.Append(" ORDER BY c.createdAtUtc DESC, c.id DESC");

        var definition = new QueryDefinition(sql.ToString());
        foreach (var (name, value) in query) definition = definition.WithParameter(name, value);

        var rows = await CosmosQuery.ReadAsync<OrderSummaryRow>(Container, definition, customerId, pageSize + 1, ct);
        return new Page<OrderSummary>(
            rows.Take(pageSize).Select(r => new OrderSummary(Guid.Parse(r.Id), r.CreatedAtUtc, r.Status, r.ItemCount, r.Total,
                r.Lines.Select(l => new OrderLine(l.ProductId, l.ProductName, l.FranchiseCode, l.UnitPrice, l.Quantity, l.Type, l.ImageUrl)).ToList())).ToList(),
            rows.Count > pageSize);
    }

    /// <summary>
    /// Two small fields per order from the customer's own partition, summed in the API. A customer has tens to hundreds of
    /// orders, so this is cheap; it avoids depending on aggregate support that differs between Cosmos and the emulator.
    /// </summary>
    public async Task<IReadOnlyList<(DateTime CreatedAtUtc, decimal Total)>> GetTotalsAsync(string customerId, CancellationToken ct)
    {
        var definition = new QueryDefinition("SELECT c.createdAtUtc, c.total FROM c WHERE c.customerId = @customerId AND c.docType = @docType")
            .WithParameter("@customerId", customerId).WithParameter("@docType", DocTypes.Order);
        var rows = await CosmosQuery.ReadAsync<TotalRow>(Container, definition, customerId, 10_000, ct);
        return rows.Select(r => (r.CreatedAtUtc, r.Total)).ToList();
    }

    private sealed class OrderSummaryRow
    {
        public string Id { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public OrderStatus Status { get; set; }
        public int ItemCount { get; set; }
        public decimal Total { get; set; }
        public List<OrderLineDocument> Lines { get; set; } = [];
    }

    private sealed class TotalRow
    {
        public DateTime CreatedAtUtc { get; set; }
        public decimal Total { get; set; }
    }
}
