# BackWave.Conformance

The Storage Contract Conformance Suite for [BackWave](https://backwave.app). If you are writing a storage
adapter, this package certifies it against the exact behavior the engine relies on: claims, leases,
retries, ordering, Transactional Enqueue, and the concurrency edges. A passing adapter is a correct one.

```csharp
using BackWave.Conformance;

// Subclass the suite and hand it a factory for your store. The inherited xUnit facts run the full
// contract against it.
public sealed class MyAdapterConformanceTests : ConformanceSuite
{
    protected override IJobStore CreateStore() => new MyJobStore(/* test connection */);
}
```

Run `dotnet test` and the whole contract executes against your store. This is the same suite the
first-party Postgres, SQL Server, and SQLite adapters are certified with.

## Notes

- Brings xUnit with it, so reference it from a test project.
- The suites are abstract, so `dotnet test` will not try to run them until a concrete subclass supplies
  a store factory.

Full documentation and the adapter-author guide: https://backwave.app
