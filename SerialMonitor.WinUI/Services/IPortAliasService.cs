namespace SerialMonitor.WinUI.Services;

public interface IPortAliasService
{
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken);

    Task SetAsync(string portName, string alias, CancellationToken cancellationToken);
}
