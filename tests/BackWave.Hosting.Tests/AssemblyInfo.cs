using Xunit;

// ExecutionOutcomeTelemetryTests and ObserverDispatchServiceTests install PROCESS-GLOBAL telemetry
// captures - a MeterListener on the static BackWave meter and an ActivityListener subscribed by name
// to the BackWave ActivitySource. Both see EVERY host running anywhere in the process, and many other
// classes in this assembly start hosts. Per-Wire-Name tag filtering can isolate metrics but not span
// capture, so a host draining in a parallel class both bleeds spans into the capture and can starve
// this test's own span out of a single-item assert. Serialising the assembly is the simple, robust
// fix - no host ever runs concurrently with a global capture. Mirrors BackWave.Testing.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
