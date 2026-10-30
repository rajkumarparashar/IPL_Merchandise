using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IPLMerchandise.Application.Common;

/// <summary>
/// Tracing and metrics hooks. Any OpenTelemetry exporter (or dotnet-counters) can subscribe to "IPLMerchandise".
/// Tags never contain personal data.
/// </summary>
public static class StoreTelemetry
{
    public const string Name = "IPLMerchandise";

    public static readonly ActivitySource ActivitySource = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OrdersPlaced = Meter.CreateCounter<long>("store.orders.placed");
    public static readonly Counter<long> CheckoutsRejected = Meter.CreateCounter<long>("store.checkout.rejected");
    public static readonly Counter<long> ConcurrencyConflicts = Meter.CreateCounter<long>("store.concurrency.conflicts");
    public static readonly Counter<long> IdempotentReplays = Meter.CreateCounter<long>("store.idempotency.replays");
    public static readonly Counter<long> OrdersRecovered = Meter.CreateCounter<long>("store.orders.recovered");
    public static readonly Histogram<int> CheckoutAttempts = Meter.CreateHistogram<int>("store.checkout.attempts");
}
