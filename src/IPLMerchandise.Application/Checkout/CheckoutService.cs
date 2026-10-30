using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Carts;
using CartLineIssue = IPLMerchandise.Domain.Carts.CartLineIssue;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;
using IPLMerchandise.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Application.Checkout;

/// <summary><see cref="Created"/> is false when an earlier attempt with the same order ID already placed the order.</summary>
public sealed record CheckoutResult(OrderDto Order, bool Created);

/// <summary>
/// Turns the cart into an order. The guarantees and how they are met:
/// <list type="bullet">
/// <item>Never oversell: stock is deducted in one transaction where every product write is conditional on the version
///   we validated (ETag). If any product changed, nothing is written and we re-validate from fresh data.</item>
/// <item>All or nothing: the same transaction writes the audit entries and a commit record; any failure rolls back all of it.</item>
/// <item>No duplicate orders: the commit record's id is the client's order ID, so a second deduction for the same ID is impossible.
///   Retries find the existing order (or the commit record) and return it.</item>
/// <item>Rejections keep the cart: validation happens before any write, and the cart is only touched after the order exists.</item>
/// </list>
/// </summary>
public sealed class CheckoutService(
    ICartRepository carts,
    IProductRepository products,
    ICheckoutRepository checkout,
    IOrderRepository orders,
    IProductChangeNotifier changes,
    TimeProvider clock,
    ILogger<CheckoutService> logger)
{
    internal const int MaxAttempts = 5;

    public async Task<CheckoutResult> PlaceOrderAsync(string customerId, CheckoutRequest request, CancellationToken ct)
    {
        var orderId = request.OrderId ?? Guid.Empty;
        if (orderId == Guid.Empty)
            throw RequestValidationException.For("orderId", "A client-generated order ID is required.");

        var address = ToShippingAddress(request.ShippingAddress);

        using var activity = StoreTelemetry.ActivitySource.StartActivity("checkout");
        activity?.SetTag("order.id", orderId);

        // Retry of a request that already succeeded (e.g. the response was lost on the network).
        var existing = await orders.GetAsync(customerId, orderId, ct);
        if (existing is not null)
            return Replayed(existing);

        // Stock was committed but the process stopped before the order document was written: finish the job.
        var earlierCommit = await checkout.GetCommitAsync(orderId, ct);
        if (earlierCommit is not null)
            return Replayed(await CompleteAsync(EnsureOwnedBy(earlierCommit, customerId), ct));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var cart = await carts.GetAsync(customerId, ct);
            if (cart.IsEmpty)
                throw Reject(ErrorCodes.CartEmpty, "Your cart is empty.");

            var current = await products.GetManyAsync(cart.Lines.Select(l => l.ProductId).ToList(), ct);
            var review = cart.Review(current);
            EnsureFulfillable(review);

            if (review.RequiresReview)
                throw Reject(ErrorCodes.CartRequiresReview,
                    "Prices in your cart have changed. Review and accept the new total before checking out.",
                    new Dictionary<string, object?> { ["cart"] = review.ToDto() });

            var now = clock.GetUtcNow().UtcDateTime;
            var order = Order.Place(orderId, customerId,
                review.Lines.Select(l => new OrderLine(l.Line.ProductId, l.Current!.Name, l.Current.FranchiseCode, l.Current.Price, l.Line.Quantity, l.Current.Type, l.Current.Image?.Url)).ToList(),
                now, address, Domain.Orders.PaymentMethod.CashOnDelivery);

            if (order.Total != request.ExpectedTotal)
                throw Reject(ErrorCodes.TotalMismatch,
                    "The order total has changed since you last viewed your cart. Please review it and try again.",
                    new Dictionary<string, object?> { ["expectedTotal"] = request.ExpectedTotal, ["actualTotal"] = order.Total });

            var ctx = new ChangeContext($"customer:{customerId}", ChangeReason.Order, now, $"Order {orderId:D}", orderId.ToString("D"));
            var audit = order.Lines.Select(l => current[l.ProductId].DeductStock(l.Quantity, ctx)).ToList();
            var touched = order.Lines.Select(l => current[l.ProductId]).ToList();

            var result = await checkout.CommitAsync(touched, audit, new CheckoutCommit(order, CommitStatus.Pending), ct);
            switch (result)
            {
                case CommitResult.Committed:
                    StoreTelemetry.CheckoutAttempts.Record(attempt);
                    StoreTelemetry.OrdersPlaced.Add(1);
                    logger.LogInformation("Order {OrderId} committed for {CustomerId}: {LineCount} lines, total {Total} (attempt {Attempt})",
                        orderId, customerId, order.Lines.Count, order.Total, attempt);
                    // Stock went down: other shoppers holding these products may now be short (e.g. the last signed photo).
                    await changes.ProductsChangedAsync(order.Lines.Select(l => l.ProductId).ToList());
                    return new CheckoutResult((await CompleteAsync(new CheckoutCommit(order, CommitStatus.Pending), ct)).ToDto(), Created: true);

                case CommitResult.AlreadyCommitted:
                    // A concurrent retry with the same order ID won. Return its order.
                    var winner = await checkout.GetCommitAsync(orderId, ct)
                                 ?? throw new ConflictException(ErrorCodes.ConcurrencyConflict, "This order is still being processed. Please retry with the same order ID.");
                    return Replayed(await CompleteAsync(EnsureOwnedBy(winner, customerId), ct));

                case CommitResult.ConcurrencyConflict:
                    // Someone else bought or an admin edited one of these products. Re-read and re-validate.
                    StoreTelemetry.ConcurrencyConflicts.Add(1, new KeyValuePair<string, object?>("operation", "checkout"));
                    logger.LogInformation("Checkout {OrderId} hit a stock conflict on attempt {Attempt}; retrying", orderId, attempt);
                    await Task.Delay(Backoff(attempt), clock, ct);
                    continue;
            }
        }

        // Extreme contention on the same products. Nothing was written, so the client can safely retry with the same order ID.
        throw Reject(ErrorCodes.ConcurrencyConflict, "This item is in very high demand right now. Please try again in a moment.");
    }

    /// <summary>
    /// Second half of checkout, after stock is committed: write the order, tidy the cart, mark the commit complete.
    /// Every step is idempotent so the request path and <see cref="PendingCheckoutRecovery"/> can both run it.
    /// Once the order exists the customer must be told it succeeded, so failures in the tidy-up steps are logged and
    /// left for recovery (the commit stays Pending) instead of failing the request.
    /// </summary>
    public async Task<Order> CompleteAsync(CheckoutCommit commit, CancellationToken ct)
    {
        var order = await orders.CreateIfNotExistsAsync(commit.Order, ct);
        try
        {
            await RemovePurchasedFromCartAsync(order, ct);
            await checkout.MarkCompletedAsync(order.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Order {OrderId} is placed but post-checkout tidy-up failed; recovery will retry", order.Id);
        }
        return order;
    }

    private async Task RemovePurchasedFromCartAsync(Order order, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var cart = await carts.GetAsync(order.CustomerId, ct);
            if (!cart.RemovePurchased(order, clock.GetUtcNow().UtcDateTime)) return;
            if (await carts.SaveAsync(cart, null, ct) == CartSaveResult.Saved) return;
        }
        throw new ConflictException(ErrorCodes.ConcurrencyConflict, "The cart kept changing while removing purchased items.");
    }

    /// <summary>Field formats are checked by DataAnnotations on the DTO; the state list is a business rule checked here.</summary>
    private static ShippingAddress? ToShippingAddress(ShippingAddressDto? a)
    {
        if (a is null) return null;
        var state = IndianStates.All.FirstOrDefault(s => string.Equals(s, a.State?.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw RequestValidationException.For("shippingAddress.state", "Choose a state or union territory from the list.");
        return new ShippingAddress(a.FullName.Trim(), a.Mobile.Trim(), a.Line1.Trim(),
            string.IsNullOrWhiteSpace(a.Line2) ? null : a.Line2.Trim(), a.City.Trim(), state, a.PinCode.Trim());
    }

    private static void EnsureFulfillable(CartReview review)
    {
        var failing = review.Lines.Where(l => !l.CanFulfil).Select(l => new CheckoutFailureLine(
            l.Line.ProductId,
            l.Current?.Name ?? l.Line.ProductName,
            l.Line.Quantity,
            l.AvailableStock,
            l.Issues.HasFlag(CartLineIssue.Unavailable) ? ErrorCodes.ProductUnavailable
            : l.Issues.HasFlag(CartLineIssue.OutOfStock) ? "out-of-stock"
            : ErrorCodes.InsufficientStock)).ToList();

        if (failing.Count == 0) return;

        StoreTelemetry.CheckoutsRejected.Add(1, new KeyValuePair<string, object?>("reason", "stock"));
        var names = string.Join(", ", failing.Select(f => f.Available == 0 ? $"{f.ProductName} (out of stock)" : $"{f.ProductName} (only {f.Available} left)"));
        throw new ConflictException(ErrorCodes.CheckoutRejected,
            $"Some items cannot be supplied: {names}. Nothing was charged and your cart has been kept.",
            new Dictionary<string, object?> { [ProblemExtensions.FailingLines] = failing });
    }

    private static ConflictException Reject(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        StoreTelemetry.CheckoutsRejected.Add(1, new KeyValuePair<string, object?>("reason", code));
        return new ConflictException(code, message, extensions);
    }

    private static CheckoutCommit EnsureOwnedBy(CheckoutCommit commit, string customerId) =>
        commit.Order.CustomerId == customerId
            ? commit
            : throw new ConflictException(ErrorCodes.OrderIdConflict, "This order ID is already in use. Generate a new one.");

    private CheckoutResult Replayed(Order order)
    {
        StoreTelemetry.IdempotentReplays.Add(1, new KeyValuePair<string, object?>("operation", "checkout"));
        logger.LogInformation("Checkout replay for existing order {OrderId}", order.Id);
        return new CheckoutResult(order.ToDto(), Created: false);
    }

    /// <summary>Exponential backoff with full jitter: spreads competing buyers out instead of retrying in lockstep.</summary>
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, 20 * (1 << attempt)));
}
