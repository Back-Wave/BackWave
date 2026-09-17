using BackWave.Conformance;
using BackWave.Storage;
using BackWave.Storage.InMemory;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace BackWave.Tests;

/// <summary>
/// Proves the suite's capability gate: a clause gated on an optional hook fails when the subclass
/// declares the capability but leaves the hook at its default (or the reverse), and skips visibly when
/// the capability is undeclared. The subclasses below are private nested types, so xunit never
/// discovers them as suites of their own; each clause runs here as a plain method call.
/// </summary>
public sealed class ConformanceCapabilityGateTests
{
    [Fact]
    public async Task DeclaredCapability_WhoseHookReturnsTheDefault_FailsTheClause()
    {
        var suite = new DeclaresFaultInjectionWithoutTheHook(new RecordingOutput());

        var failure = await Assert.ThrowsAsync<FailException>(
            suite.Clause_4_Claim_CrashBeforeCommit_RollsBackTheLease_NeverHalfClaimed);

        Assert.Contains(nameof(ConformanceCapabilities.FaultInjection), failure.Message);
        Assert.Contains("CreateFaultArmedStoreAsync", failure.Message);
        Assert.Contains("returned its default", failure.Message);
    }

    [Fact]
    public async Task ProvidedHook_WithoutItsDeclaration_FailsTheClause()
    {
        var suite = new ProvidesTheHookWithoutDeclaringIt(new RecordingOutput());

        var failure = await Assert.ThrowsAsync<FailException>(
            suite.Clause_4_Claim_CrashBeforeCommit_RollsBackTheLease_NeverHalfClaimed);

        Assert.Contains(nameof(ConformanceCapabilities.FaultInjection), failure.Message);
        Assert.Contains("does not declare", failure.Message);
    }

    [Fact]
    public async Task UndeclaredCapability_WithTheHookAtItsDefault_SkipsTheClause_AndSaysSo()
    {
        var output = new RecordingOutput();
        var suite = new DeclaresNothing(output);

        await suite.Clause_4_Claim_CrashBeforeCommit_RollsBackTheLease_NeverHalfClaimed();

        var line = Assert.Single(output.Lines);
        Assert.StartsWith("skipped: FaultInjection not declared", line);
        Assert.Contains("Clause_4_Claim_CrashBeforeCommit_RollsBackTheLease_NeverHalfClaimed", line);
    }

    private sealed class DeclaresFaultInjectionWithoutTheHook(ITestOutputHelper output) : ConformanceSuite(output)
    {
        protected override ConformanceCapabilities Capabilities => ConformanceCapabilities.FaultInjection;

        protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
            => ValueTask.FromResult<IJobStore>(new InMemoryJobStore(historyPolicy: historyPolicy));
    }

    private sealed class ProvidesTheHookWithoutDeclaringIt(ITestOutputHelper output) : ConformanceSuite(output)
    {
        protected override ConformanceCapabilities Capabilities => ConformanceCapabilities.None;

        protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
            => ValueTask.FromResult<IJobStore>(new InMemoryJobStore(historyPolicy: historyPolicy));

        protected override ValueTask<IJobStore?> CreateFaultArmedStoreAsync(string failpoint)
            => ValueTask.FromResult<IJobStore?>(new InMemoryJobStore());
    }

    private sealed class DeclaresNothing(ITestOutputHelper output) : ConformanceSuite(output)
    {
        protected override ConformanceCapabilities Capabilities => ConformanceCapabilities.None;

        protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
            => ValueTask.FromResult<IJobStore>(new InMemoryJobStore(historyPolicy: historyPolicy));
    }

    private sealed class RecordingOutput : ITestOutputHelper
    {
        public List<string> Lines { get; } = [];

        public void WriteLine(string message) => Lines.Add(message);

        public void WriteLine(string format, params object[] args) => Lines.Add(string.Format(format, args));
    }
}
