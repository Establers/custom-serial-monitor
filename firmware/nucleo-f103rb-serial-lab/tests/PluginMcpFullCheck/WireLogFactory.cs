using Microsoft.Extensions.Logging;

// SDK-owned protocol logging; this observes messages without rewriting or
// constructing JSON-RPC and records cancellation request IDs for diagnosis.
sealed class WireLogFactory(string directory) : ILoggerFactory
{
    private readonly object gate=new();
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName)=>new WireLog(this,categoryName);
    public void Dispose() { }
    private void Write(string category,string text)
    {
        lock(gate)File.AppendAllText(Path.Combine(directory,"sdk-wire.log"),DateTimeOffset.Now.ToString("o")+" "+category+" "+text+Environment.NewLine);
    }
    private sealed class WireLog(WireLogFactory owner,string category):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel logLevel,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
            =>owner.Write(category,formatter(state,exception));
    }
}
