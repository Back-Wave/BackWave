using Xunit;

// These tests install PROCESS-GLOBAL telemetry captures - a MeterListener on the static BackWave
// meter (ObservabilityTests) and an SDK InMemoryExporter subscribed by name to the BackWave
// ActivitySource (CoreInstrumentationRegistrationTests). Both see EVERY harness running anywhere in
// the process, so a harness draining in a parallel test class leaks its metrics and spans into the
// capture: extra measurements fail single-measurement asserts, and the exporter's List<Activity> is
// mutated mid-enumeration. Tag filtering can isolate metrics but not span capture. Serialising the
// assembly is the simple, robust fix - no harness ever runs concurrently with a global capture.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
