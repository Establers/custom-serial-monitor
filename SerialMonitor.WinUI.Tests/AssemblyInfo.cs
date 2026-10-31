// Independent tests deliberately block mock serial/filesystem workers and assert
// short startup/cancellation deadlines. Running those tests concurrently on the
// same small ThreadPool measures cross-test starvation instead of the behavior
// under test. Serialize test cases; keep all explicit in-test concurrency intact.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
