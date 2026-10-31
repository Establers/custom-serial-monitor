// Blocking cancellation mocks share the runner's ThreadPool; process probes
// also compete for CPU. On low-core CI they can delay another test's short
// cancellation/startup deadline. Serialize independent cases while retaining
// every test's explicit internal concurrency.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
