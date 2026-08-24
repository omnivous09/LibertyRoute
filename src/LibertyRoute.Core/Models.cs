namespace LibertyRoute.Core;

public enum ConnectionState
{
    Disconnected,
    CapturingState,
    SnapshotCommitted,
    Connecting,
    Connected,
    RollbackRequired,
    RollingBack,
    Verifying,
    RestorationFailed
}

public sealed record DnsInterfaceState(
    string InterfaceId,
    string InterfaceName,
    bool IsUp,
    IReadOnlyList<string> DnsServers);

public sealed record GatewayState(string InterfaceId, string Address);

public sealed record AdapterState(
    string Id,
    string Name,
    string Description,
    string NetworkInterfaceType,
    string OperationalStatus,
    IReadOnlyList<string> UnicastAddresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);

public sealed record NetworkStateSnapshot(
    DateTimeOffset CapturedAtUtc,
    string MachineName,
    IReadOnlyList<AdapterState> Adapters,
    IReadOnlyList<RouteState>? Routes = null);

public sealed record OwnedNetworkChange(
    Guid ChangeId,
    string Kind,
    string Target,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset RecordedAtUtc);

public sealed record NetworkTransaction(
    Guid SessionId,
    ConnectionState State,
    DateTimeOffset StartedAtUtc,
    NetworkStateSnapshot Snapshot,
    IReadOnlyList<OwnedNetworkChange> Changes,
    string? EngineId,
    string? LastError);

public sealed record VpnServerConfig(
    string Id,
    string DisplayName,
    string CountryCode,
    string Host,
    int Port,
    string ServerPublicKey,
    string ClientAddress,
    IReadOnlyList<string> AllowedIps,
    string? DnsServer,
    int? Mtu);
