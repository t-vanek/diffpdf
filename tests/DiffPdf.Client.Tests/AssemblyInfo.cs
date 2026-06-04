// Each test boots a full in-memory API host (WebApplicationFactory). Running multiple hosts in
// parallel makes the run-now → report integration tests timing-sensitive, so disable cross-class
// parallelization for this assembly — these are end-to-end tests, not fast isolated unit tests.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
