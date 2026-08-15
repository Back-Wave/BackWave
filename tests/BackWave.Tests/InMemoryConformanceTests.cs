using System.Data.Common;
using BackWave.Conformance;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// The Conformance Suite against the In-Memory Store — the reference implementation must
/// pass 100% before any adapter runs it (spec §10).
/// </summary>
public sealed class InMemoryConformanceTests : ConformanceSuite
{
    protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
        => ValueTask.FromResult<IJobStore>(new InMemoryJobStore(historyPolicy: historyPolicy));

    protected override DbTransaction BeginTransaction(IJobStore store)
        => ((InMemoryJobStore)store).BeginTransaction();
}
