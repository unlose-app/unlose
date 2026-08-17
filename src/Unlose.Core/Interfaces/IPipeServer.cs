using Unlose.Core.Models;

namespace Unlose.Core.Interfaces;

public interface IPipeServer
{
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task BroadcastAsync(IUnloseEvent evt, CancellationToken ct = default);
}
