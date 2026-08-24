using LibertyRoute.Core;

namespace LibertyRoute.Engine;

public interface IConnectionEngine
{
    string Id { get; }
    Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}
