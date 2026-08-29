using System.Diagnostics;

namespace BackWave.Torture;

/// <summary>
/// The mid-run audit pass: a background loop that walks the Transition Log forward by
/// <c>position</c> while the workload is still hammering the store, so a violation is caught near
/// its cause instead of minutes later behind a drain.
///
/// There is no barrier and no quiescent point - the clients never stop - so the pass runs only the
/// checks that stay sound against a moving store. A per-row check reads one atomic row, and a
/// journal-only check is monotone under append, so neither can be torn by a concurrent write. Every
/// check that compares two separately-read sources stays in the post-drain <see cref="Auditor"/>.
///
/// Each pass reads the transition and its job row in ONE joined statement, so both halves come from
/// a single snapshot. The cursor moves forward only: a row committed behind the cursor is simply
/// never seen, which shows up as an ordinal gap, and a gap is not treated as an edge.
/// </summary>
internal sealed class MidRunAudit(
    ITortureTarget target, Journal journal, KeySpace keys, TortureOptions options, ViolationSink violations)
{
    private const int PageRows = 2_000;

    // The last transition seen per job. Within one job, position order IS ordinal order (transitions
    // are appended by sequential transactions), so carrying the tail across passes is sound.
    private readonly Dictionary<Guid, TransitionFacts> _lastSeen = [];

    // The pass's own growing copy of the journal, extended by the delta each time: re-copying all of
    // it every pass is the one cost that would grow without bound.
    private readonly List<JournalEntry> _journalView = [];

    private long _cursor;

    /// <summary>Seed-derived, 5 to 20 seconds: a fixed period would put a predictable load pattern on the store.</summary>
    public TimeSpan Interval { get; } =
        TimeSpan.FromSeconds(5 + (long)(SplitMix64.Next(options.Seed ^ 0xA0D17UL) % 16));

    public int Passes { get; private set; }

    public long TransitionsWalked { get; private set; }

    /// <summary>Slots dropped because the previous pass overran them - queued passes compound under pressure.</summary>
    public int Skips { get; private set; }

    public TimeSpan Cost { get; private set; }

    /// <summary>Runs until the time box ends, cancelling it on the first violation so the drain never runs.</summary>
    public async Task RunAsync(CancellationTokenSource timebox)
    {
        var clock = Stopwatch.StartNew();
        var next = Interval;
        try
        {
            while (!timebox.IsCancellationRequested)
            {
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timebox.Token);
                }

                await PassAsync(timebox.Token);
                if (violations.Count > 0)
                {
                    Console.WriteLine("torture: mid-run audit found a violation - cutting the time box short.");
                    await timebox.CancelAsync();
                    return;
                }

                next += Interval;
                while (next <= clock.Elapsed)
                {
                    next += Interval;
                    Skips++;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var rows = await target.ReadChangeFeedAsync(_cursor, PageRows, cancellationToken);
            foreach (var row in rows)
            {
                Walk(row);
            }
            TransitionsWalked += rows.Count;
            if (rows.Count > 0)
            {
                _cursor = rows[^1].Position;
            }
            if (rows.Count < PageRows)
            {
                break;
            }
        }

        var watermark = journal.Watermark;
        if (watermark > _journalView.Count)
        {
            _journalView.AddRange(journal.Range(_journalView.Count, watermark - _journalView.Count));
        }
        Checks.LiveJournal(_journalView, violations);

        Passes++;
        Cost += timer.Elapsed;
    }

    private void Walk(AuditRow row)
    {
        Checks.JobRow(row.Job, keys, options, violations);
        if (_lastSeen.TryGetValue(row.JobId, out var previous))
        {
            Checks.TransitionEdge(row.JobId, previous, row.Transition, options.MaxAttempts, violations);
        }
        else
        {
            Checks.InitialTransition(row.JobId, row.Transition, violations);
        }
        _lastSeen[row.JobId] = row.Transition;
    }
}
