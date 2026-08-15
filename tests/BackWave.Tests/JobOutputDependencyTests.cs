using BackWave.Jobs;
using BackWave.Storage;
using BackWave.Storage.InMemory;

namespace BackWave.Tests;

/// <summary>
/// The Job Output <b>read path</b> (ADR 0026, issue 0132): a job <b>pulls</b> the output of its
/// transitive Dependency ancestors via <see cref="JobContext.GetDependencyOutputAsync"/> — River's
/// <c>LoadDeps</c>. Resolution walks the immutable structural Workflow edges UP from the reader, so an
/// ancestor of an already-released job still resolves (the live gating edges have resolved away); a
/// node name resolves against the ancestor set by the stored Wire Name, a raw Dependency by JobId, and
/// a non-ancestor sibling is physically unresolvable (the scope guarantee). In-Memory Store only — the
/// Core, Node Driver, and Simulator are untouched (this is above-the-store read logic). Reuses the
/// <see cref="Receipt"/> / <see cref="OutputJsonContext"/> from <see cref="JobOutputTests"/>.
/// </summary>
public class JobOutputDependencyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    private static NewJob Member(string wireName, params Guid[] parents) => new(
        Guid.NewGuid(), wireName, ReadOnlyMemory<byte>.Empty, "wf", T0) { Parents = [.. parents] };

    /// <summary>A handler-execution context wired exactly as the Shell wires it, over the given store.</summary>
    private static JobContext ContextFor(InMemoryJobStore store, Guid jobId) => new()
    {
        JobId = jobId,
        Attempt = 1,
        DependencyResolver = new StoreDependencyResolver(store),
    };

    /// <summary>
    /// Drives one specific due job to Succeeded and writes <paramref name="output"/>. A claim leases a
    /// whole batch, so the target may already be Leased from a prior call (fan-in/fan-out have several
    /// roots due at once); claim only when it is not yet held, then report on the live Attempt.
    /// </summary>
    private static async Task SucceedWithOutput(
        InMemoryJobStore store, Guid jobId, DateTimeOffset now, Receipt? output)
    {
        var record = await store.GetJobAsync(jobId);
        if (record!.State != JobState.Leased)
        {
            await store.ClaimAsync(new ClaimRequest("w1", ["wf"], 32, Lease, now));
            record = await store.GetJobAsync(jobId);
        }
        Assert.Equal(JobState.Leased, record!.State);
        var blob = output is null
            ? (ReadOnlyMemory<byte>?)null
            : JobOutputCodec.Encode(output, OutputJsonContext.Default.Receipt);
        Assert.Equal(
            OutcomeResult.Applied,
            await store.ReportOutcomeAsync(jobId, "w1", record.Attempt, new JobOutcome.Success(), now, output: blob));
    }

    // ── Transitive resolution: A → B → C reads A, not only B ───────────────────────

    [Fact]
    public async Task Chain_C_ReadsTransitiveAncestorA()
    {
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        var c = Member("c", b.JobId);
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b, c] }, T0));

        // A succeeds with output; B then succeeds (releasing C). A's gating edge to B is now gone.
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("from-a", 1));
        await SucceedWithOutput(store, b.JobId, T0, new Receipt("from-b", 2));

        // C — now released and running — still resolves A transitively (two hops up).
        var ctx = ContextFor(store, c.JobId);
        var fromA = await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        var fromB = await ctx.GetDependencyOutputAsync("b", OutputJsonContext.Default.Receipt);

        Assert.True(fromA.HasOutput);
        Assert.Equal(new Receipt("from-a", 1), fromA.Output);
        Assert.True(fromB.HasOutput);
        Assert.Equal(new Receipt("from-b", 2), fromB.Output);
    }

    // ── Exhaustive over chain / fan-out / fan-in ───────────────────────────────────

    [Fact]
    public async Task FanIn_AllAncestorsResolveByName()
    {
        // a, b → c (fan-in). c reads both parents.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b");
        var c = Member("c", a.JobId, b.JobId);
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b, c] }, T0));
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("a", 1));
        await SucceedWithOutput(store, b.JobId, T0, new Receipt("b", 2));

        var ctx = ContextFor(store, c.JobId);
        Assert.Equal(new Receipt("a", 1), (await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt)).Output);
        Assert.Equal(new Receipt("b", 2), (await ctx.GetDependencyOutputAsync("b", OutputJsonContext.Default.Receipt)).Output);
    }

    [Fact]
    public async Task FanOut_DiamondReadsTheSharedRoot()
    {
        // a → b, a → c, (b, c) → d. d reads the shared root a transitively through both branches.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        var c = Member("c", a.JobId);
        var d = Member("d", b.JobId, c.JobId);
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b, c, d] }, T0));
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("root", 9));
        await SucceedWithOutput(store, b.JobId, T0, new Receipt("b", 1));
        await SucceedWithOutput(store, c.JobId, T0, new Receipt("c", 2));

        var ctx = ContextFor(store, d.JobId);
        Assert.Equal(new Receipt("root", 9), (await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt)).Output);
        Assert.Equal(new Receipt("b", 1), (await ctx.GetDependencyOutputAsync("b", OutputJsonContext.Default.Receipt)).Output);
    }

    // ── Scope guarantee: a non-ancestor sibling is unresolvable ────────────────────

    [Fact]
    public async Task NonAncestorSibling_IsUnresolvable()
    {
        // a → b, a → sibling. b and sibling are siblings (no edge between them). b cannot read sibling.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        var sibling = Member("sibling", a.JobId);
        Assert.Equal(WorkflowEnqueueResult.Ok, await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b, sibling] }, T0));
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("a", 1));
        await SucceedWithOutput(store, sibling.JobId, T0, new Receipt("should-not-be-readable", 7));

        var ctx = ContextFor(store, b.JobId);
        // The ancestor (a) resolves; the parallel sibling does not — the racy read is physically
        // unwritable, surfaced as a clean absence (no output, no throw).
        Assert.True((await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt)).HasOutput);
        var sib = await ctx.GetDependencyOutputAsync("sibling", OutputJsonContext.Default.Receipt);
        Assert.False(sib.HasOutput);
        Assert.Null(sib.Output);
    }

    // ── Raw-Dependency JobId path (no names) ───────────────────────────────────────

    [Fact]
    public async Task RawDependency_ResolvesByJobId()
    {
        // A plain (non-workflow) Dependency: child waits on parent; child pulls parent's output by id.
        var store = new InMemoryJobStore();
        var parent = new NewJob(Guid.NewGuid(), "parent", ReadOnlyMemory<byte>.Empty, "wf", T0);
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(parent, T0));
        var child = new NewJob(Guid.NewGuid(), "child", ReadOnlyMemory<byte>.Empty, "wf", T0)
        {
            Parents = [parent.JobId],
        };
        Assert.Equal(EnqueueResult.Ok, await store.EnqueueAsync(child, T0));
        await SucceedWithOutput(store, parent.JobId, T0, new Receipt("raw", 42));

        var ctx = ContextFor(store, child.JobId);
        var result = await ctx.GetDependencyOutputAsync(parent.JobId.ToString(), OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.Succeeded, result.AncestorState);
        Assert.True(result.HasOutput);
        Assert.Equal(new Receipt("raw", 42), result.Output);
    }

    [Fact]
    public async Task UnknownJobId_ResolvesToAbsence()
    {
        var store = new InMemoryJobStore();
        var solo = new NewJob(Guid.NewGuid(), "solo", ReadOnlyMemory<byte>.Empty, "wf", T0);
        await store.EnqueueAsync(solo, T0);

        var ctx = ContextFor(store, solo.JobId);
        var result = await ctx.GetDependencyOutputAsync(Guid.NewGuid().ToString(), OutputJsonContext.Default.Receipt);
        Assert.False(result.HasOutput);
    }

    // ── Terminal state alongside the output; absence on non-success ────────────────

    [Fact]
    public async Task SucceededAncestor_ReturnsBlobAndState()
    {
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("ok", 5));

        var result = await ContextFor(store, b.JobId).GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.Succeeded, result.AncestorState);
        Assert.True(result.HasOutput);
        Assert.Equal(new Receipt("ok", 5), result.Output);
    }

    [Fact]
    public async Task SucceededAncestorWithNoOutput_ReturnsAbsenceWithState()
    {
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);
        await SucceedWithOutput(store, a.JobId, T0, output: null); // succeeded, emitted nothing

        var result = await ContextFor(store, b.JobId).GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.Succeeded, result.AncestorState); // state still surfaced
        Assert.False(result.HasOutput);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task FailedAncestor_ReturnsAbsenceWithFailedState()
    {
        // OnAnyTerminal: b runs even though a failed — the canonical "branch on did-it-succeed?" case.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId) with { Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);

        // a fails to terminal (MaxAttempts default exhausts on a no-NextDueTime failure → DeadLettered).
        var claimed = await store.ClaimAsync(new ClaimRequest("w1", ["wf"], 32, Lease, T0));
        var aJob = claimed.Single(j => j.JobId == a.JobId);
        await store.ReportOutcomeAsync(aJob.JobId, "w1", aJob.Attempt, new JobOutcome.Failure(null, "boom"), T0);
        Assert.Equal(JobState.DeadLettered, (await store.GetJobAsync(a.JobId))!.State);

        var result = await ContextFor(store, b.JobId).GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.DeadLettered, result.AncestorState);
        Assert.False(result.HasOutput);
    }

    [Fact]
    public async Task CancelledAncestor_ReturnsAbsenceWithCancelledState()
    {
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId) with { Mode = DependencyMode.OnAnyTerminal };
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);

        // Cancel a while still Scheduled → immediate terminal Cancelled.
        Assert.Equal(CancelResult.CancelledImmediately, await store.CancelJobAsync(a.JobId, "operator", T0));

        var result = await ContextFor(store, b.JobId).GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.Cancelled, result.AncestorState);
        Assert.False(result.HasOutput);
    }

    // ── v1.2.0 compatibility: a stale zero-length Output blob reads as absence ─────

    [Fact]
    public async Task ZeroLengthOutputBlob_ReadsAsAbsence_NotADecodeThrow()
    {
        // Shipped v1.2.0 persisted a non-null EMPTY Output blob on every silent success (a write-side
        // cast bug, since fixed). A fleet upgraded in place still carries those stale 0-byte rows; the
        // read side must treat them exactly like null - no output, no JsonException.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);

        // Succeed a with an explicit EMPTY (non-null, zero-length) output blob - the v1.2.0 row shape.
        await store.ClaimAsync(new ClaimRequest("w1", ["wf"], 32, Lease, T0));
        var record = await store.GetJobAsync(a.JobId);
        Assert.Equal(OutcomeResult.Applied, await store.ReportOutcomeAsync(
            a.JobId, "w1", record!.Attempt, new JobOutcome.Success(), T0, output: ReadOnlyMemory<byte>.Empty));

        var result = await ContextFor(store, b.JobId).GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(JobState.Succeeded, result.AncestorState);
        Assert.False(result.HasOutput);
        Assert.Null(result.Output);
    }

    // ── Lazy: no speculative pre-load ──────────────────────────────────────────────

    [Fact]
    public async Task Accessor_ReadsOnlyWhenCalled()
    {
        // A context built but never queried resolves nothing — the resolver fires only on the call,
        // and once per call, so the Node driver does no speculative pre-load.
        var store = new InMemoryJobStore();
        var a = Member("a");
        var b = Member("b", a.JobId);
        await store.EnqueueWorkflowAsync(
            new WorkflowDefinition { WorkflowId = Guid.NewGuid(), Members = [a, b] }, T0);
        await SucceedWithOutput(store, a.JobId, T0, new Receipt("a", 1));

        var resolver = new CountingResolver(new StoreDependencyResolver(store));
        var ctx = new JobContext { JobId = b.JobId, Attempt = 1, DependencyResolver = resolver };
        Assert.Equal(0, resolver.Calls); // constructing the context resolved nothing

        await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt);
        Assert.Equal(1, resolver.Calls); // exactly one resolution, on demand
    }

    [Fact]
    public async Task ContextWithoutResolver_ThrowsOnRead()
    {
        // The write-only context (e.g. the last-write-wins SetOutput test) has no resolver wired;
        // attempting to read ancestor output is a programmer error, surfaced loudly.
        var ctx = new JobContext { JobId = Guid.NewGuid(), Attempt = 1 };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ctx.GetDependencyOutputAsync("a", OutputJsonContext.Default.Receipt));
    }

    /// <summary>A resolver decorator that counts resolutions, to prove the accessor reads only on call.</summary>
    private sealed class CountingResolver(IDependencyResolver inner) : IDependencyResolver
    {
        public int Calls;

        public ValueTask<ResolvedDependencyOutput?> ResolveAsync(
            Guid readerJobId, string nameOrJobId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.ResolveAsync(readerJobId, nameOrJobId, cancellationToken);
        }
    }
}
