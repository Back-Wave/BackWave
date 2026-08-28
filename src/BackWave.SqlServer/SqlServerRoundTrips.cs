using Microsoft.Data.SqlClient;

namespace BackWave.SqlServer;

// Counts what a SQL Server store operation actually costs the network: the statements it executes.
// That single number is a latency figure that needs no clock, because Microsoft.Data.SqlClient sends
// one TDS request per execute and waits for the server's response before the await completes. There is
// no client-side batching underneath - two ExecuteNonQueryAsync calls are two requests on the wire even
// on the same open connection - so statements executed IS round trips paid, and multiplying by the link
// latency gives the operation's floor.
//
// That cost is invisible to every other test in the suite. Against a co-located container a batched
// write and a per-row loop finish in the same blink, so a per-row regression ships green and shows up
// only as a slightly lower job rate against a real network. A count separates them deterministically,
// which is what makes it safe to gate CI on.
//
// This is instrumentation, not a feature: it is internal, reached only through [InternalsVisibleTo], and
// it is inert unless a test is observing. Every hook reads one volatile int and returns, so an
// unobserved process allocates nothing, executes no extra statement, and touches no AsyncLocal.
internal static class SqlServerRoundTrips
{
    // How many scopes are live anywhere in the process. Checked FIRST by every hook, before the
    // AsyncLocal lookup, which is the only part of the path with real cost. Production never observes,
    // so production never pays more than this read.
    private static int _observers;

    // The scope belonging to the async flow in flight. An AsyncLocal rather than a static counter
    // because a budget belongs to ONE operation: ExecutionContext carries the scope down through every
    // await inside the store call and no further, so work on a detached background task can never
    // contribute to the number under test.
    private static readonly AsyncLocal<SqlServerRoundTripScope?> Flow = new();

    // Begins observing this async flow. Dispose the returned scope to stop; the counts stay readable
    // afterwards. Scopes nest (an inner scope shadows the outer one) so a helper can measure a sub-step.
    internal static SqlServerRoundTripScope Observe()
    {
        var scope = new SqlServerRoundTripScope(Flow.Value);
        Flow.Value = scope;
        Interlocked.Increment(ref _observers);
        return scope;
    }

    internal static void EndScope(SqlServerRoundTripScope scope)
    {
        Flow.Value = scope.Outer;
        Interlocked.Decrement(ref _observers);
    }

    // One request left for the server. Counted at EXECUTION, never at construction: a round trip happens
    // when a statement runs, and SqlServerJobStore.Cmd - the one place a command is BUILT - is not the
    // one place a command RUNS (the dynamic-SQL query builders assemble theirs inline, and a command may
    // be built and then never executed, or executed more than once). Counted BEFORE the call, so a
    // statement that comes back as a SqlException still counts: it made the trip.
    //
    // A multi-statement batch - the adapter sends several, separated by semicolons, in one CommandText -
    // is deliberately ONE count, because the wire carries it as one request. That is the whole point of
    // writing it that way, and the budget should credit it.
    internal static void CountStatement()
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.AddStatement();
    }

    // The adapter's execution entry points. Every ExecuteXxxAsync in the adapter goes through one of
    // these, and that is enforced rather than trusted: SqlServerRoundTripSeamTests fails the build's
    // test pass on any bare driver call in src/BackWave.SqlServer that is neither a wrapper body below
    // nor marked "uncounted round trip:" at the call site with a reason. The marked ones are statements
    // belonging to no operation - migration and the schema-version probe.
    internal static Task<int> ExecuteNonQueryCountedAsync(
        this SqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task<SqlDataReader> ExecuteReaderCountedAsync(
        this SqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteReaderAsync(cancellationToken);
    }

    internal static Task<object?> ExecuteScalarCountedAsync(
        this SqlCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteScalarAsync(cancellationToken);
    }
}

// The counts an observed async flow accumulated. Mutated through Interlocked because the store is free
// to fan a single operation across tasks; reading is only meaningful once the work being measured has
// completed, and the counts remain readable after Dispose so a test can assert outside the using block.
internal sealed class SqlServerRoundTripScope(SqlServerRoundTripScope? outer) : IDisposable
{
    private int _statements;
    private bool _disposed;

    internal SqlServerRoundTripScope? Outer { get; } = outer;

    internal int Statements => Volatile.Read(ref _statements);

    internal void AddStatement() => Interlocked.Increment(ref _statements);

    public void Dispose()
    {
        if (_disposed)
        {
            return; // a second Dispose must not unbalance the observer count
        }
        _disposed = true;
        SqlServerRoundTrips.EndScope(this);
    }
}
