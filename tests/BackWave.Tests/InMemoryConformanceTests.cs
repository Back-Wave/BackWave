using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Xunit.Abstractions;

namespace BackWave.Tests;

/// <summary>
/// The Conformance Suite against the In-Memory Store — the reference implementation must
/// pass every clause it declares before any adapter runs it (spec §10). It declares the store-level
/// capabilities only (NextDue, lease hand-back, named observer outcomes): it holds no transaction to
/// abort or park and no state column to overwrite, so the crash-mid-write, forced-interleaving, held-row,
/// and out-of-band state clauses are skipped here and certified on the SQL adapters.
/// </summary>
[Collection(InvariantViolationCounterCollection.Name)] // raises the observer-fence violation a neighbor reads
public sealed class InMemoryConformanceTests(ITestOutputHelper output) : ConformanceSuite(output)
{
    protected override ConformanceCapabilities Capabilities
        => ConformanceCapabilities.NextDue
        | ConformanceCapabilities.LeaseRelinquish
        | ConformanceCapabilities.ObserverReportOutcomes;

    protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
        => ValueTask.FromResult<IJobStore>(new InMemoryJobStore(historyPolicy: historyPolicy));

    protected override DbTransaction BeginTransaction(IJobStore store)
        => ((InMemoryJobStore)store).BeginTransaction();
}
