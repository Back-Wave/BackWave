namespace BackWave.Conformance;

// Contributor breadcrumb: the failpoint hook is issue 0034; each SQL adapter's test-only hook
// throws this when armed via ConformanceSuite.CreateFaultArmedStoreAsync.
/// <summary>
/// Thrown by a store's armed test-only failpoint to abort an in-flight transaction between the
/// effects of a multi-effect operation. The crash-mid-write conformance tests arm a failpoint
/// through <see cref="ConformanceSuite.CreateFaultArmedStoreAsync"/> and expect exactly this
/// exception out of the faulted operation, then assert that none of the operation's effects
/// survived. An adapter's failpoint hook should throw this type — nothing else — so the suite can
/// tell an injected fault apart from a real failure.
/// </summary>
/// <param name="failpoint">The name of the failpoint that tripped, quoted in the exception message.</param>
public sealed class FaultInjectedException(string failpoint)
    : Exception($"Test-only failpoint '{failpoint}' tripped.");
