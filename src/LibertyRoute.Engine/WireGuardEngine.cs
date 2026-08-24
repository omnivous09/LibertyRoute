using LibertyRoute.Core;

namespace LibertyRoute.Engine;

/// <summary>
/// Phase 1 boundary for WireGuard's Windows embeddable-dll-service integration.
/// It deliberately performs no network mutation until the rollback/recovery
/// foundation is proven on the target Windows machine.
/// </summary>
public sealed class WireGuardEngine : IConnectionEngine
{
    public string Id => "wireguard-windows-embeddable";

    public Task StartAsync(VpnServerConfig server, CancellationToken cancellationToken)
        => throw new NotSupportedException(
            "WireGuard transport is the Phase 2 milestone. Configure the official Windows embeddable-dll-service before enabling Connect.");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(false);
}
