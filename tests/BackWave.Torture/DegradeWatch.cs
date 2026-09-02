using System.Diagnostics.Metrics;
using BackWave.Diagnostics;

namespace BackWave.Torture;

/// <summary>
/// Reads the Degrade half of the production fail-stop vocabulary and journals it.
///
/// The Halt half carries itself: a halt site throws <c>InvariantViolationException</c>, which
/// <see cref="WorkloadClient"/> and the drain/audit already catch and journal by trigger id. A Degrade
/// throws nothing - it counts the impossible state, takes its benign branch and returns a normal result -
/// and every adapter site passes a null logger, because no adapter holds an <c>ILogger</c>. That leaves the
/// <c>backwave.invariant.violations</c> counter as the ONE surface a degraded trigger reaches, which is
/// exactly what <c>Invariant.Degrade</c> says the count is for, so the suite subscribes to it.
///
/// Degrading is a production decision about staying in service, not a licence for the state to exist. The
/// suite is a discovery instrument with no benign branch: a tripped trigger is a finding whichever way the
/// site handled it, so each measurement lands in the journal and <see cref="Checks.DegradedTriggers"/>
/// turns it into a <see cref="TortureInvariant.DegradeTriggerFired"/> violation named by trigger.
///
/// Journaling rather than tallying privately is what makes it work across the SQLite multi-process shape:
/// a child's counter lives in the child's process, and its journal is merged into the parent's.
/// </summary>
internal sealed class DegradeWatch : IDisposable
{
    private const string CounterName = "backwave.invariant.violations";
    private const string TriggerKey = "backwave.invariant.trigger";
    private const string ActionKey = "backwave.invariant.action";
    private const string DegradeAction = "Degrade";

    private readonly Journal _journal;
    private readonly string _client;
    private readonly MeterListener _listener;

    public DegradeWatch(Journal journal, string client)
    {
        _journal = journal;
        _client = client;
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BackWaveDiagnostics.SourceName && instrument.Name == CounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(
        Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        // Halt measurements are ignored on purpose. They are emitted by the Hosting pump's fail-stop catch,
        // which no torture process runs, and the throw that provoked them is already journaled by the client
        // that saw it - reading them here would only risk naming the same halt twice.
        string? trigger = null;
        var degraded = false;
        foreach (var tag in tags)
        {
            if (tag.Key == TriggerKey)
            {
                trigger = tag.Value as string;
            }
            else if (tag.Key == ActionKey)
            {
                degraded = (tag.Value as string) == DegradeAction;
            }
        }

        if (!degraded)
        {
            return;
        }

        // Recorded on whichever thread made the store call, so the entry sits in the journal beside the
        // operation that tripped it. The counter carries tags only, so the site's message is not available
        // here - the trigger id is the identity that matters, and it is what the finding names.
        // One entry per counted violation: Invariant.Degrade always adds exactly one, but a counter's
        // Add() takes a delta, and the journal's count is what the finding reports.
        var now = DateTimeOffset.UtcNow.UtcTicks;
        for (var i = 0; i < value; i++)
        {
            _journal.Record(new JournalEntry
            {
                Client = _client, Op = Ops.InvariantDegrade, T0 = now, T1 = now,
                Result = trigger ?? "Unknown",
                Detail = "counted on backwave.invariant.violations; the site logs its message only where a logger is wired",
            });
        }
    }
}
