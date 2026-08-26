using Oracle.ManagedDataAccess.Client;

namespace BackWave.Oracle;

// Counts what an Oracle store operation actually costs the network: the statements it executes and the
// LOB values it materializes. Both are per-round-trip on this driver - ODP.NET sends one statement per
// round trip, and a LOB column left at the default InitialLOBFetchSize of 0 arrives as a locator whose
// value costs another - so a count here is a latency figure that needs no clock and no network. That
// matters because the cost this measures is invisible to a co-located test container: a per-row round
// trip and a batched one are indistinguishable at sub-millisecond latency, and only a count separates
// them. The adapter's own suite pins the numbers so an optimization has to move them on purpose.
//
// This is instrumentation, not a feature: it is internal, reached only through [InternalsVisibleTo], and
// it is inert unless a test is observing. Every hook reads one volatile int and returns, so an
// unobserved process allocates nothing, executes no extra statement, and touches no AsyncLocal.
internal static class OracleRoundTrips
{
    // How many scopes are live anywhere in the process. Checked FIRST by every hook, before the
    // AsyncLocal lookup, which is the only part of the path with real cost. Production never observes,
    // so production never pays more than this read.
    private static int _observers;

    // The scope belonging to the async flow in flight. An AsyncLocal rather than a static counter
    // because a budget belongs to ONE operation: ExecutionContext carries the scope down through every
    // await inside the store call and no further, so work on a detached background task - the Wake-Up
    // Hint pump's own session, say - can never contribute to the number under test.
    private static readonly AsyncLocal<OracleRoundTripScope?> Flow = new();

    // Begins observing this async flow. Dispose the returned scope to stop; the counts stay readable
    // afterwards. Scopes nest (an inner scope shadows the outer one) so a helper can measure a sub-step.
    internal static OracleRoundTripScope Observe()
    {
        var scope = new OracleRoundTripScope(Flow.Value);
        Flow.Value = scope;
        Interlocked.Increment(ref _observers);
        return scope;
    }

    internal static void EndScope(OracleRoundTripScope scope)
    {
        Flow.Value = scope.Outer;
        Interlocked.Decrement(ref _observers);
    }

    // One statement left for the server. Counted at EXECUTION, never at construction: a round trip
    // happens when a statement runs, and OracleJobStore.Cmd - the one place a command is BUILT - is not
    // the one place a command RUNS (the dynamic-SQL query builders assemble theirs inline, and a command
    // may be built and then never executed, or executed more than once). Counted BEFORE the call, so a
    // statement that comes back as an ORA- error still counts: it made the trip.
    internal static void CountStatement()
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.AddStatement();
    }

    // One LOB value pulled across the wire. Counts READS only - the write side binds a Clob/Blob
    // parameter, which is a parameter cost rather than a materialization, and it is the read side that
    // the default zero LOB fetch size penalizes. A NULL LOB is not a read: nothing is fetched, which is
    // exactly why a page of TERMINAL jobs (non-null terminal_cause) costs so much more than a live one.
    internal static void CountLobRead()
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.AddLobRead();
    }

    // One LOB value, counted only if the wire actually carried it on its own. A value the driver
    // prefetched arrived inside the row and cost nothing further, so counting it would report a round
    // trip that never happened. InitialLOBFetchSize is the prefetch size the command asked for - bytes
    // for a BLOB, characters for a CLOB - which is the unit `length` is measured in on each read path.
    // A zero-length LOB is never prefetched: an empty prefetch buffer cannot be told apart from an
    // absent one, so the driver follows the locator for it and pays the trip.
    internal static void CountLobRead(OracleDataReader reader, int length)
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        if (length == 0 || length > reader.InitialLOBFetchSize)
        {
            Flow.Value?.AddLobRead();
        }
    }

    // The widest fetch window a command in this flow declared. FetchSize is the driver's byte budget for
    // one fetch round trip, so this one number is both the memory a read command may hold in flight and
    // the size of the unit a fetch trip moves. Recorded as a maximum rather than counted: what a budget
    // needs to know is the largest buffer an operation asked the driver to hold.
    internal static void RecordFetchWindow(long bytes)
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.RecordFetchWindow(bytes);
    }

    // The adapter's execution entry points. Every ExecuteXxxAsync in the adapter goes through one of
    // these, and that is enforced rather than trusted: OracleRoundTripSeamTests fails the build's test
    // pass on any bare driver call in src/BackWave.Oracle that is neither a wrapper body below nor
    // marked "uncounted round trip:" at the call site with a reason. The marked ones are statements
    // belonging to no operation - the Wake-Up Hint pump's parked session, and migration.
    internal static Task<int> ExecuteNonQueryCountedAsync(
        this OracleCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task<OracleDataReader> ExecuteReaderCountedAsync(
        this OracleCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteReaderAsync(cancellationToken);
    }

    internal static Task<object?> ExecuteScalarCountedAsync(
        this OracleCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteScalarAsync(cancellationToken);
    }
}

// The counts an observed async flow accumulated. Mutated through Interlocked because the store is free
// to fan a single operation across tasks; reading is only meaningful once the work being measured has
// completed, and the counts remain readable after Dispose so a test can assert outside the using block.
internal sealed class OracleRoundTripScope(OracleRoundTripScope? outer) : IDisposable
{
    private int _statements;
    private int _lobReads;
    private long _fetchWindow;
    private bool _disposed;

    internal OracleRoundTripScope? Outer { get; } = outer;

    internal int Statements => Volatile.Read(ref _statements);

    internal int LobReads => Volatile.Read(ref _lobReads);

    internal long FetchWindowBytes => Volatile.Read(ref _fetchWindow);

    internal void AddStatement() => Interlocked.Increment(ref _statements);

    internal void AddLobRead() => Interlocked.Increment(ref _lobReads);

    internal void RecordFetchWindow(long bytes)
    {
        var seen = Volatile.Read(ref _fetchWindow);
        while (bytes > seen)
        {
            var prior = Interlocked.CompareExchange(ref _fetchWindow, bytes, seen);
            if (prior == seen)
            {
                return;
            }
            seen = prior;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return; // a second Dispose must not unbalance the observer count
        }
        _disposed = true;
        OracleRoundTrips.EndScope(this);
    }
}
