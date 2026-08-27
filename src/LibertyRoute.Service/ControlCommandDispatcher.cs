using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;

namespace LibertyRoute.Service;

internal sealed class ControlCommandDispatcher : IControlCommandDispatcher
{
    private readonly ConnectionController _controller;

    public ControlCommandDispatcher(ConnectionController controller)
        => _controller = controller ?? throw new ArgumentNullException(nameof(controller));

    public async Task<ControlDispatchResult> DispatchAsync(ControlCallerIdentity caller, ControlRequestEnvelope request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ControlResponseResult result = request.Command switch
        {
            ControlCommand.Status => new ControlStatusResult(MapState(_controller.State)),
            ControlCommand.Snapshot => new ControlSnapshotResult(MapSnapshot(await _controller.CaptureDiagnosticSnapshotAsync(cancellationToken))),
            ControlCommand.Connect => new ControlConnectResult(MapState((await _controller.BeginSafeConnectAsync(caller.UserSid, cancellationToken)).State)),
            ControlCommand.Disconnect => await DisconnectAsync(cancellationToken),
            _ => throw new InvalidOperationException("The control command is not supported.")
        };
        return new ControlDispatchResult(ControlOutcome.Succeeded, ControlErrorCode.None, result);
    }

    internal static ControlConnectionState MapState(ConnectionState state) => state switch
    {
        ConnectionState.Disconnected => ControlConnectionState.Disconnected,
        ConnectionState.CapturingState => ControlConnectionState.CapturingState,
        ConnectionState.SnapshotCommitted => ControlConnectionState.SnapshotCommitted,
        ConnectionState.Connecting => ControlConnectionState.Connecting,
        ConnectionState.Connected => ControlConnectionState.Connected,
        ConnectionState.RollbackRequired => ControlConnectionState.RollbackRequired,
        ConnectionState.RollingBack => ControlConnectionState.RollingBack,
        ConnectionState.Verifying => ControlConnectionState.Verifying,
        ConnectionState.RestorationFailed => ControlConnectionState.RestorationFailed,
        _ => throw new InvalidOperationException("The connection state is not supported by the control protocol.")
    };

    internal static ControlNetworkSnapshot MapSnapshot(NetworkStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ControlNetworkSnapshot(snapshot.CapturedAtUtc, Required(snapshot.MachineName, nameof(snapshot.MachineName)),
            RequiredItems(snapshot.Adapters, nameof(snapshot.Adapters)).Select(MapAdapter).ToArray(),
            snapshot.Routes?.Select(MapRoute).ToArray(), snapshot.DnsInterfaces?.Select(MapDnsInterface).ToArray());
    }

    private async Task<ControlDisconnectResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        await _controller.RollbackAsync("User requested disconnect.", cancellationToken);
        return new ControlDisconnectResult(MapState(_controller.State));
    }

    private static ControlAdapterState MapAdapter(AdapterState adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new ControlAdapterState(Required(adapter.Id, nameof(adapter.Id)), Required(adapter.Name, nameof(adapter.Name)),
            Required(adapter.Description, nameof(adapter.Description)), Required(adapter.NetworkInterfaceType, nameof(adapter.NetworkInterfaceType)),
            Required(adapter.OperationalStatus, nameof(adapter.OperationalStatus)), RequiredStrings(adapter.UnicastAddresses, nameof(adapter.UnicastAddresses)),
            RequiredStrings(adapter.Gateways, nameof(adapter.Gateways)), RequiredStrings(adapter.DnsServers, nameof(adapter.DnsServers)));
    }

    private static ControlRouteState MapRoute(RouteState route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new ControlRouteState(Required(route.Destination, nameof(route.Destination)), Required(route.NextHop, nameof(route.NextHop)),
            route.InterfaceIndex, route.Metric, Required(route.AddressFamily, nameof(route.AddressFamily)));
    }

    private static ControlDnsInterfaceState MapDnsInterface(DnsInterfaceState dns)
    {
        ArgumentNullException.ThrowIfNull(dns);
        return new ControlDnsInterfaceState(Required(dns.InterfaceId, nameof(dns.InterfaceId)), Required(dns.InterfaceName, nameof(dns.InterfaceName)),
            dns.IsUp, RequiredStrings(dns.DnsServers, nameof(dns.DnsServers)), NullableStrings(dns.IPv4DnsServers, nameof(dns.IPv4DnsServers)),
            NullableStrings(dns.IPv6DnsServers, nameof(dns.IPv6DnsServers)), MapDnsSource(dns.IPv4ConfigurationSource),
            MapDnsSource(dns.IPv6ConfigurationSource), NullableStrings(dns.IPv4StaticDnsServers, nameof(dns.IPv4StaticDnsServers)),
            NullableStrings(dns.IPv4DhcpDnsServers, nameof(dns.IPv4DhcpDnsServers)), NullableStrings(dns.IPv6StaticDnsServers, nameof(dns.IPv6StaticDnsServers)),
            NullableStrings(dns.IPv6DhcpDnsServers, nameof(dns.IPv6DhcpDnsServers)));
    }

    internal static ControlDnsConfigurationSource MapDnsSource(DnsConfigurationSource source) => source switch
    {
        DnsConfigurationSource.Unknown => ControlDnsConfigurationSource.Unknown,
        DnsConfigurationSource.Automatic => ControlDnsConfigurationSource.Automatic,
        DnsConfigurationSource.Static => ControlDnsConfigurationSource.Static,
        DnsConfigurationSource.Mixed => ControlDnsConfigurationSource.Mixed,
        _ => throw new InvalidOperationException("The DNS configuration source is not supported by the control protocol.")
    };

    private static string Required(string? value, string name) => value ?? throw new InvalidOperationException($"The diagnostic {name} value is null.");
    private static IReadOnlyList<T> RequiredItems<T>(IReadOnlyList<T>? values, string name) where T : class
    {
        if (values is null || values.Any(value => value is null)) throw new InvalidOperationException($"The diagnostic {name} collection is invalid.");
        return values;
    }
    private static IReadOnlyList<string> RequiredStrings(IReadOnlyList<string>? values, string name)
        => NullableStrings(values, name) ?? throw new InvalidOperationException($"The diagnostic {name} collection is null.");
    private static IReadOnlyList<string>? NullableStrings(IReadOnlyList<string>? values, string name)
    {
        if (values is null) return null;
        if (values.Any(value => value is null)) throw new InvalidOperationException($"The diagnostic {name} collection contains a null value.");
        return values.ToArray();
    }
}
