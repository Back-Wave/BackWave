using Xunit;

// Teardown in this assembly calls the process-global SqliteConnection.ClearAllPools(), which races any
// pooled connection held by a test in another class.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
