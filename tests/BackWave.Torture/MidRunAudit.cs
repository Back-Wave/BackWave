using System.Diagnostics;
using BackWave.Diagnostics;

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

    // The journal-only oracle, carried across passes as counters. Each pass folds in only the entries
    // appended since the last one, so a pass costs its delta and never the run's whole history.
    private readonly JournalOracle _oracle = new();

    // How far into the journal the oracle has read.
    private int _absorbed;

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

                try
                {
                    await PassAsync(timebox.Token);
                }
                catch (OperationCanceledException) when (timebox.IsCancellationRequested)
                {
                    return;
                }
                catch (InvariantViolationException)
                {
                    // A production fail-stop trigger, which the caller turns into a finding. Rethrown
                    // ahead of every classifier below on purpose: an adapter's classifier reads provider
                    // fault codes, and a halt trigger must never be demoted to contention noise.
                    throw;
                }
                catch (Exception exception) when (target.IsTransientFault(exception))
                {
                    // The audit drives the same adapter the clients do, under the same contention, so it
                    // meets the same deadlocks and timeouts they do. That is noise, not evidence: journal
                    // it and take the next pass. Nothing propagates, because an escape here would skip the
                    // artifact bundle the whole run exists to produce.
                    journal.Record(new JournalEntry
                    {
                        Client = "mid-run-audit", Op = Ops.TransientFault,
                        T0 = DateTimeOffset.UtcNow.UtcTicks, T1 = DateTimeOffset.UtcNow.UtcTicks,
                        Result = "audit-pass", Detail = exception.GetType().Name,
                    });
                }
                catch (Exception exception)
                {
                    // A raw provider exception out of the store surface is itself a finding, so it is
                    // recorded as one instead of ending the run. The check below then cuts the time box,
                    // which is the same path every other mid-run finding takes.
                    violations.Add(new TortureViolation(
                        TortureInvariant.RawStoreException,
                        $"The mid-run audit read threw {exception.GetType().FullName} out of the store " +
                        $"surface: {exception.Message}"));
                }
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
        if (watermark > _absorbed)
        {
            _oracle.Absorb(journal.Range(_absorbed, watermark - _absorbed));
            _absorbed = watermark;
        }
        _oracle.Evaluate(violations);

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
