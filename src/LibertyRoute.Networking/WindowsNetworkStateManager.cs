using System.Net.NetworkInformation;
using LibertyRoute.Core;
using LibertyRoute.Networking.Native;

namespace LibertyRoute.Networking;

/// <summary>
/// Phase 1 implementation is intentionally read-only. It captures the state needed
/// to prove the transaction/recovery path before privileged mutations are introduced.
/// </summary>
public sealed class WindowsNetworkStateManager : INetworkStateManager
{
    public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(ni =>
            {
                var props = ni.GetIPProperties();
                var unicast = props.UnicastAddresses.Select(x => x.Address.ToString()).OrderBy(x => x).ToArray();
                var gateways = props.GatewayAddresses.Select(x => x.Address.ToString()).OrderBy(x => x).ToArray();
                var dns = props.DnsAddresses.Select(x => x.ToString()).OrderBy(x => x).ToArray();

                return new AdapterState(
                    ni.Id,
                    ni.Name,
                    ni.Description,
                    ni.NetworkInterfaceType.ToString(),
                    ni.OperationalStatus.ToString(),
                    unicast,
                    gateways,
                    dns);
            })
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(new NetworkStateSnapshot(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            adapters,
            WindowsRouteTableReader.Capture()));
    }

    public async Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
    {
        var current = await CaptureStateAsync(cancellationToken);

        // Dynamic DHCP addresses, lease data and transient OS routes are not treated as
        // LibertyRoute-owned state. Phase 1 verifies that all originally present adapter
        // identities remain observable and reports a failure otherwise.
        var currentIds = current.Adapters.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = original.Adapters.Where(a => !currentIds.Contains(a.Id)).Select(a => a.Name).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Restoration verification could not find original adapters: " + string.Join(", ", missing));
        }
    }
}
