using LibertyRoute.Core;

namespace LibertyRoute.Networking;

public interface INetworkStateManager
{
    Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken);
    Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken);
}
