using BackWave.Diagnostics;
using BackWave.Storage;
using Microsoft.Extensions.Hosting;

namespace BackWave.Hosting;

/// <summary>
/// Feeds the <c>backwave.queue.depth</c> gauge: one registration per host (not per Worker
/// Group — groups sharing the store must not double-count), refreshing a cached snapshot
/// on a fixed cadence. Metrics are never load-bearing: a failed refresh keeps the last
/// snapshot and never halts anything.
/// </summary>
internal sealed class BackWaveMetricsService(IJobStore store) : BackgroundService
{
    /// <summary>Named bound: one depths query per host per interval, whether or not anyone exports.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<QueueStateCount> depths = [];
        using var registration = BackWaveDiagnostics.RegisterQueueDepthSource(() => depths);
        using var timer = new PeriodicTimer(RefreshInterval);
        do
        {
            try
            {
                depths = await store.CountJobsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Keep the previous snapshot; the next tick retries.
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
