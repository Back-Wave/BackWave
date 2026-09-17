# BackWave.Conformance

The Storage Contract Conformance Suite for [BackWave](https://backwave.app). If you are writing a storage
adapter, this package certifies it against the exact behavior the engine relies on: claims, leases,
retries, ordering, Transactional Enqueue, and the concurrency edges. A passing adapter is a correct one.

```csharp
using BackWave.Conformance;

// Subclass the suite, hand it a factory for your store, and declare the optional capabilities your
// subclass provides. The inherited xUnit facts run the full contract against it.
public sealed class MyAdapterConformanceTests(ITestOutputHelper output) : ConformanceSuite(output)
{
    protected override ConformanceCapabilities Capabilities
        => ConformanceCapabilities.NextDue | ConformanceCapabilities.LeaseRelinquish;

    protected override ValueTask<IJobStore> CreateStoreAsync(JobHistoryPolicy historyPolicy)
        => new(new MyJobStore(/* fresh, empty test database */, historyPolicy));
}
```

Run `dotnet test` and the whole contract executes against your store. This is the same suite the
first-party Postgres, SQL Server, SQLite, and Oracle adapters are certified with.

## Declaring capabilities

The contract has a mandatory spine and a set of optional capabilities: a real next-due hint, lease
hand-back, named observer refusals, atomic batch outcomes, and the test-only hooks that let the suite
inject a fault, force an interleaving, hold a row uncommitted, or write a job state out of band.
`Capabilities` is abstract, so every subclass states which of those it provides, and the clauses that
need one are gated on that declaration:

- Declared, and the hook or store feature is there: the clause runs in full.
- Declared, but the hook still returns its default: the clause fails, naming the hook. A lapsed
  override can never pass in silence.
- Not declared: the clause returns early and writes `skipped: <capability> not declared` to the test
  output when the subclass passes xunit's `ITestOutputHelper` through to the base constructor.
- Not declared, but the hook produces a value anyway: the clause fails too, so the declaration stays
  exact in both directions.

Each `ConformanceCapabilities` flag documents the hook it pairs with and what implementing it proves.

## Notes

- Brings xUnit with it, so reference it from a test project.
- The suites are abstract, so `dotnet test` will not try to run them until a concrete subclass supplies
  a store factory and a capability declaration.

Full documentation and the adapter-author guide: https://backwave.app
