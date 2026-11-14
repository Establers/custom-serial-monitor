using System.Collections.Concurrent;

// Exercise the ViewModel's captured-context continuations on one test thread,
// as in the GUI, without creating a window or automating any desktop controls.
internal sealed class ScenarioContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new(4096);
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));

    public static Task RunAsync(Func<Task> action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var context = new ScenarioContext();
            SetSynchronizationContext(context);
            try
            {
                var work = action();
                _ = work.ContinueWith(_ => context._queue.CompleteAdding(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var item in context._queue.GetConsumingEnumerable()) item.Callback(item.State);
                work.GetAwaiter().GetResult();
                result.TrySetResult();
            }
            catch (Exception error) { result.TrySetException(error); }
            finally { SetSynchronizationContext(null); }
        }) { IsBackground = true, Name = "Modbus user scenario context" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    public void Dispose() => _queue.Dispose();
}
