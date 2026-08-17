namespace Unlose.Core.Interfaces;

public interface IPipeClient
{
    Task<string> SendCommandAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken ct = default);
}
