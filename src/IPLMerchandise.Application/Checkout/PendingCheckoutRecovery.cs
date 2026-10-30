using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using Microsoft.Extensions.Logging;

namespace IPLMerchandise.Application.Checkout;

/// <summary>
/// Finishes checkouts whose stock was committed but whose order document was never written (process crash, timeout).
/// Without this, a customer who never retries would lose stock without an order. Run periodically by a hosted service.
/// </summary>
public sealed class PendingCheckoutRecovery(
    ICheckoutRepository checkout,
    CheckoutService checkoutService,
    TimeProvider clock,
    ILogger<PendingCheckoutRecovery> logger)
{
    /// <summary>Commits younger than this are probably still being completed by their own request.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(1);

    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        var pending = await checkout.GetPendingAsync(clock.GetUtcNow().UtcDateTime - GracePeriod, max: 50, ct);
        var recovered = 0;
        foreach (var commit in pending)
        {
            try
            {
                await checkoutService.CompleteAsync(commit, ct);
                recovered++;
                StoreTelemetry.OrdersRecovered.Add(1);
                logger.LogWarning("Recovered pending order {OrderId}", commit.Order.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not recover pending order {OrderId}; will retry", commit.Order.Id);
            }
        }
        return recovered;
    }
}
