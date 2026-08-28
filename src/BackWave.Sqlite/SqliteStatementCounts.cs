using Microsoft.Data.Sqlite;

namespace BackWave.Sqlite;

// Counts the statements a SQLite store operation executes. Be plain about what that number is and is
// not, because the same instrument on a client/server adapter measures something else entirely.
//
// SQLite is in-process. There is no socket, no server, and no round trip: a statement here costs a
// prepare (or a prepared-statement cache hit) plus one or more steps of the virtual machine, run on the
// calling thread against a memory-mapped file, and a write statement pays that under the connection's
// write lock. What it does NOT cost is a network trip, so nothing in this file may be read as a latency
// figure the way the Oracle counter's can be. A statement here is cheap in absolute terms.
//
// Pinning it is still worth doing, for one reason: it is the only instrument in the suite that can tell
// a batched write apart from a per-row loop. Both are functionally identical, both pass conformance, and
// against a local file both finish in a blink - so no timing test and no behavioural test can see the
// difference. The difference is real anyway, because SQLite has exactly one writer. A per-row loop over
// N rows holds the write lock for N prepares and N steps where a set-based statement holds it for one,
// and the whole time it holds it every other pump, every enqueue, and every dashboard write in the
// process is queued behind it. Serializing the fleet on the write lock is precisely how a SQLite
// deployment falls over, and a statement count is what makes an edit that reintroduces it visible.
//
// This is instrumentation, not a feature: it is internal, reached only through [InternalsVisibleTo], and
// it is inert unless a test is observing. The hook reads one volatile int and returns, so an unobserved
// process allocates nothing, executes no extra statement, and touches no AsyncLocal.
internal static class SqliteStatementCounts
{
    // How many scopes are live anywhere in the process. Checked FIRST by the hook, before the AsyncLocal
    // lookup, which is the only part of the path with real cost. Production never observes, so production
    // never pays more than this read.
    private static int _observers;

    // The scope belonging to the async flow in flight. An AsyncLocal rather than a static counter because
    // a budget belongs to ONE operation: ExecutionContext carries the scope down through every await
    // inside the store call and no further, so work on a detached background task can never contribute to
    // the number under test.
    private static readonly AsyncLocal<SqliteStatementScope?> Flow = new();

    // Begins observing this async flow. Dispose the returned scope to stop; the counts stay readable
    // afterwards. Scopes nest (an inner scope shadows the outer one) so a helper can measure a sub-step.
    internal static SqliteStatementScope Observe()
    {
        var scope = new SqliteStatementScope(Flow.Value);
        Flow.Value = scope;
        Interlocked.Increment(ref _observers);
        return scope;
    }

    internal static void EndScope(SqliteStatementScope scope)
    {
        Flow.Value = scope.Outer;
        Interlocked.Decrement(ref _observers);
    }

    // One statement run against the database. Counted at EXECUTION, never at construction:
    // SqliteJobStore.Cmd - the one place a command is BUILT - is not the one place a command RUNS (the
    // dynamic-SQL query builders assemble theirs inline, and a command may be built and then never
    // executed, or executed more than once). Counted BEFORE the call, so a statement that comes back as a
    // SqliteException still counts: it was prepared and stepped, and if it was a write it held the lock.
    internal static void CountStatement()
    {
        if (Volatile.Read(ref _observers) == 0)
        {
            return;
        }
        Flow.Value?.AddStatement();
    }

    // The adapter's execution entry points. Every ExecuteXxxAsync in the adapter goes through one of
    // these, and that is enforced rather than trusted: SqliteStatementSeamTests fails the build's test
    // pass on any bare driver call in src/BackWave.Sqlite that is neither a wrapper body below nor marked
    // "uncounted round trip:" at the call site with a reason. The marked ones are statements belonging to
    // no store operation - migration, the engine-version probe, the connection-open PRAGMAs, and the
    // one-time schema-version check.
    //
    // On the marker's wording: "round trip" is Oracle's word and, as the class remarks say, a misnomer
    // here. It is kept verbatim anyway. The same seam test runs over four adapters, and one marker string
    // a reviewer can grep for across all of them is worth more than a locally accurate name that only
    // this adapter uses.
    internal static Task<int> ExecuteNonQueryCountedAsync(
        this SqliteCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task<SqliteDataReader> ExecuteReaderCountedAsync(
        this SqliteCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteReaderAsync(cancellationToken);
    }

    internal static Task<object?> ExecuteScalarCountedAsync(
        this SqliteCommand command, CancellationToken cancellationToken)
    {
        CountStatement();
        return command.ExecuteScalarAsync(cancellationToken);
    }
}

// The count an observed async flow accumulated. Mutated through Interlocked because the store is free to
// fan a single operation across tasks; reading is only meaningful once the work being measured has
// completed, and the count remains readable after Dispose so a test can assert outside the using block.
internal sealed class SqliteStatementScope(SqliteStatementScope? outer) : IDisposable
{
    private int _statements;
    private bool _disposed;

    internal SqliteStatementScope? Outer { get; } = outer;

    internal int Statements => Volatile.Read(ref _statements);

    internal void AddStatement() => Interlocked.Increment(ref _statements);

    public void Dispose()
    {
        if (_disposed)
        {
            return; // a second Dispose must not unbalance the observer count
        }
        _disposed = true;
        SqliteStatementCounts.EndScope(this);
    }
}
