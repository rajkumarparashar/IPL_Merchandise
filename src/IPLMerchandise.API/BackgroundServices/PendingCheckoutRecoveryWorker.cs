using IPLMerchandise.Application.Checkout;

namespace IPLMerchandise.API.BackgroundServices;

/// <summary>Periodically completes checkouts that committed stock but never wrote their order (see PendingCheckoutRecovery).</summary>
public sealed class PendingCheckoutRecoveryWorker(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<PendingCheckoutRecoveryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(configuration.GetValue("Checkout:RecoveryIntervalSeconds", 60));
        if (interval <= TimeSpan.Zero) return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PendingCheckoutRecovery>().RecoverAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the loop alive: a database blip must not stop recovery permanently.
                logger.LogError(ex, "Pending checkout recovery run failed");
            }
        }
    }
}
