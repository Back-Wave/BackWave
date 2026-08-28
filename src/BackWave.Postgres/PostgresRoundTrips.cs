using Npgsql;

namespace BackWave.Postgres;

// Counts what a Postgres store operation actually costs the network: the statements it executes. On the
// extended query protocol Npgsql sends one statement per round trip on a simple execute - the command is
// parsed, bound, described, executed and synced in a single flush, and the caller cannot continue until
// the server answers - so a statement count IS a latency figure, one that needs no clock and no network.
// That matters because the cost this measures is invisible to a co-located test container: a batched
// write and a per-row loop over the same rows finish in the same blink at sub-millisecond latency, and
// only a count separates them. The adapter's own suite pins the numbers so an optimization has to move
// them on purpose, and so a refactor cannot quietly hand one back.
//
// Statements are the whole instrument here, unlike Oracle's counter, which also has to watch LOB
// materialization: Postgres has no LOB locator on these paths. A bytea and a text column arrive inside
// the row like every other value, so there is no second trip hiding behind a column read and nothing for
// a fetch-window dial to trade away.
//
// This is instrumentation, not a feature: it is internal, reached only through [InternalsVisibleTo], and
// it is inert unless a test is observing. Every hook reads one volatile int and returns, so an
// unobserved process allocates nothing, executes no extra statement, and touches no AsyncLocal.
internal static class PostgresRoundTrips
{
    // How many scopes are live anywhere in the process. Checked FIRST by every hook, before the
    // AsyncLocal lookup, which is the only part of the path with real cost. Production never observes,
    // so production never pays more than this read.
    private static int _observers;

    // The scope belonging to the async flow in flight. An AsyncLocal rather than a static counter
    // because a budget belongs to ONE operation: ExecutionContext carries the scope down through every
    // await inside the store call and no further, so work on a detached background task - the Wake-Up
    // Hint subscription's own LISTEN session, say - can never contribute to the number under test.
    private static readonly AsyncLocal<PostgresRoundTripScope?> Flow = new();

    // Begins observing this async flow. Dispose the returned scope to stop; the counts stay readable
    // afterwards. Scopes nest (an inner scope shadows the outer one) so a helper can measure a sub-step.
    internal static PostgresRoundTripScope Observe()
    {
        var scope = new PostgresRoundTripScope(Flow.Value);
        Flow.Value = scope;
        Interlocked.Increment(ref _observers);
        return scope;
    }

    internal static void EndScope(PostgresRoundTripScope scope)
    {
        Flow.Value = scope.Outer;
        Interlocked.Decrement(ref _observers);
    }

    // One statement left for the server. Counted at EXECUTION, never at construction: a round trip
    // happens when a statement runs, and the place a command is BUILT is not the place it RUNS (the
    // dynamic-SQL query builders assemble theirs inline, and a command may be built and then never
    // executed, or executed more than once). Counted BEFORE the call, so a statement that comes back as
    // a PostgresException still counts: it made the trip.
    internal static void CountStatement()
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.AddStatement();
    }

    // The adapter's execution entry points. Every ExecuteXxxAsync in the adapter goes through one of
    // these, and that is enforced rather than trusted: PostgresRoundTripSeamTests fails the build's test
    // pass on any bare driver call in src/BackWave.Postgres that is neither a wrapper body below nor
    // marked "uncounted round trip:" at the call site with a reason. The marked ones are statements
    // belonging to no operation - the Wake-Up Hint subscription's parked LISTEN session, and migration.
    internal static Task<int> ExecuteNonQueryCountedAsync(
        this NpgsqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task<NpgsqlDataReader> ExecuteReaderCountedAsync(
        this NpgsqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteReaderAsync(cancellationToken);
    }

    internal static Task<object?> ExecuteScalarCountedAsync(
        this NpgsqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteScalarAsync(cancellationToken);
    }
}

// The counts an observed async flow accumulated. Mutated through Interlocked because the store is free
// to fan a single operation across tasks; reading is only meaningful once the work being measured has
// completed, and the counts remain readable after Dispose so a test can assert outside the using block.
internal sealed class PostgresRoundTripScope(PostgresRoundTripScope? outer) : IDisposable
{
    private int _statements;
    private bool _disposed;

    internal PostgresRoundTripScope? Outer { get; } = outer;

    internal int Statements => Volatile.Read(ref _statements);

    internal void AddStatement() => Interlocked.Increment(ref _statements);

    public void Dispose()
    {
        if (_disposed)
        {
            return; // a second Dispose must not unbalance the observer count
        }
        _disposed = true;
        PostgresRoundTrips.EndScope(this);
    }
}
