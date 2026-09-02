using Prometheus;

namespace Whalebone.Records.Api.Observability;

/// <summary>
/// Publishes ASP.NET Core's own HTTP instruments on a Prometheus scrape endpoint.
/// </summary>
/// <remarks>
/// Nothing here counts anything. ASP.NET Core 8 already emits request duration and in-flight
/// request counts through <c>System.Diagnostics.Metrics</c>, covering every route including the
/// ones nobody remembered to instrument; hand-rolled counters would only be a second, staler
/// copy of that. This class does two jobs: it stops the bridge publishing the entire process,
/// and it replaces bucket boundaries chosen for generic instruments with ones chosen for HTTP.
/// </remarks>
internal static class ServiceMetrics
{
    /// <summary>The meter carrying <c>http.server.request.duration</c> and <c>http.server.active_requests</c>.</summary>
    private const string HostingMeter = "Microsoft.AspNetCore.Hosting";

    /// <summary>
    /// Latency buckets in seconds. The bridge's own default is 25 exponential buckets starting at
    /// 10 ms, so the top boundary lands near 46 hours and every label combination costs 26 series
    /// to describe a request that would have been abandoned before the tenth of them.
    /// </summary>
    private static readonly double[] LatencySeconds =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    private static int _started;

    /// <summary>Starts the meter bridge. Safe to call more than once; only the first call does anything.</summary>
    /// <remarks>
    /// The Prometheus registry is process-global, but a host is not. <c>WebApplicationFactory</c>
    /// runs this entry point once per host it builds, and a second adapter feeding the same
    /// registry double-counts every measurement - a test suite would read exactly twice the truth
    /// and look plausible doing it. Idempotence is enforced here rather than left to the caller.
    /// </remarks>
    public static void StartCollecting()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        // Both bridges are opt-OUT. Left alone, prometheus-net republishes every instrument of
        // every meter in the process, plus the .NET EventCounters: hundreds of series nobody
        // asked for, labelled by libraries that never considered this endpoint's cardinality.
        Prometheus.Metrics.SuppressDefaultMetrics(new SuppressDefaultMetricOptions
        {
            // Kept: process CPU, memory and GC are the cheapest useful signal there is.
            SuppressProcessMetrics = false,
            SuppressDebugMetrics = true,
            SuppressEventCounters = true,

            // Suppressed so the filtered adapter below is the only one running.
            SuppressMeters = true,
        });

        // Started eagerly rather than through Metrics.ConfigureMeterAdapter, which defers the
        // start into a before-first-collect callback - so every request served before the first
        // scrape would be missing from it.
        MeterAdapter.StartListening(new MeterAdapterOptions
        {
            InstrumentFilterPredicate = static instrument => instrument.Meter.Name == HostingMeter,
            ResolveHistogramBuckets = static _ => LatencySeconds,
        });
    }
}
