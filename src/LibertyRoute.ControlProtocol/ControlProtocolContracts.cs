using System.Text.Json.Serialization;

namespace LibertyRoute.ControlProtocol;

public static class ControlProtocolConstants
{
    public const int Version = 2;
    public const int LengthPrefixSize = 4;
    public const int MaximumGreetingSize = 1024;
    public const int MaximumRequestSize = 16 * 1024;
    public const int MaximumResponseSize = 1024 * 1024;
}

public enum ControlCommand
{
    Status,
    Snapshot,
    Connect,
    Disconnect
}

public enum ControlOutcome
{
    Succeeded,
    Failed
}

public enum ControlConnectionState
{
    Disconnected, CapturingState, SnapshotCommitted, Connecting, Connected,
    RollbackRequired, RollingBack, Verifying, RestorationFailed
}

public enum ControlDnsConfigurationSource { Unknown, Automatic, Static, Mixed }

public enum ControlErrorCode
{
    None,
    InvalidRequest,
    UnsupportedVersion,
    Unauthorized,
    ForbiddenCommand,
    WrongServiceInstance,
    StaleRequest,
    DuplicateRequest,
    RequestConflict,
    ReplayCapacityExceeded,
    ResponseTooLarge,
    InternalError
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlServerGreeting(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] Guid ServiceInstanceId);

public enum ControlProtocolError
{
    InvalidLengthPrefix,
    ZeroLengthFrame,
    FrameTooLarge,
    TruncatedFrame,
    InvalidUtf8,
    MalformedJson,
    InvalidContract,
    UnsupportedVersion,
    UnknownCommand
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRequestPayload;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRequestEnvelope(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] Guid ServiceInstanceId,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] DateTimeOffset SentAtUtc,
    [property: JsonRequired] ControlCommand Command,
    [property: JsonRequired] ControlRequestPayload Payload);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$resultType")]
[JsonDerivedType(typeof(ControlStatusResult), "STATUS")]
[JsonDerivedType(typeof(ControlSnapshotResult), "SNAPSHOT")]
[JsonDerivedType(typeof(ControlConnectResult), "CONNECT")]
[JsonDerivedType(typeof(ControlDisconnectResult), "DISCONNECT")]
public abstract record ControlResponseResult;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlStatusResult([property: JsonRequired] ControlConnectionState State) : ControlResponseResult;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlSnapshotResult([property: JsonRequired] ControlNetworkSnapshot Snapshot) : ControlResponseResult;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlConnectResult([property: JsonRequired] ControlConnectionState State) : ControlResponseResult;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlDisconnectResult([property: JsonRequired] ControlConnectionState State) : ControlResponseResult;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlNetworkSnapshot(
    [property: JsonRequired] DateTimeOffset CapturedAtUtc,
    [property: JsonRequired] string MachineName,
    [property: JsonRequired] IReadOnlyList<ControlAdapterState> Adapters,
    [property: JsonRequired] IReadOnlyList<ControlRouteState>? Routes,
    [property: JsonRequired] IReadOnlyList<ControlDnsInterfaceState>? DnsInterfaces);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlAdapterState(
    [property: JsonRequired] string Id, [property: JsonRequired] string Name,
    [property: JsonRequired] string Description, [property: JsonRequired] string NetworkInterfaceType,
    [property: JsonRequired] string OperationalStatus, [property: JsonRequired] IReadOnlyList<string> UnicastAddresses,
    [property: JsonRequired] IReadOnlyList<string> Gateways, [property: JsonRequired] IReadOnlyList<string> DnsServers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlRouteState(
    [property: JsonRequired] string Destination, [property: JsonRequired] string NextHop,
    [property: JsonRequired] int InterfaceIndex, [property: JsonRequired] uint Metric,
    [property: JsonRequired] string AddressFamily);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlDnsInterfaceState(
    [property: JsonRequired] string InterfaceId, [property: JsonRequired] string InterfaceName,
    [property: JsonRequired] bool IsUp, [property: JsonRequired] IReadOnlyList<string> DnsServers,
    [property: JsonRequired] IReadOnlyList<string>? IPv4DnsServers, [property: JsonRequired] IReadOnlyList<string>? IPv6DnsServers,
    [property: JsonRequired] ControlDnsConfigurationSource IPv4ConfigurationSource,
    [property: JsonRequired] ControlDnsConfigurationSource IPv6ConfigurationSource,
    [property: JsonRequired] IReadOnlyList<string>? IPv4StaticDnsServers,
    [property: JsonRequired] IReadOnlyList<string>? IPv4DhcpDnsServers,
    [property: JsonRequired] IReadOnlyList<string>? IPv6StaticDnsServers,
    [property: JsonRequired] IReadOnlyList<string>? IPv6DhcpDnsServers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ControlResponseEnvelope(
    [property: JsonRequired] int ProtocolVersion,
    [property: JsonRequired] Guid ServiceInstanceId,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] ControlCommand Command,
    [property: JsonRequired] ControlOutcome Outcome,
    [property: JsonRequired] ControlErrorCode ErrorCode,
    [property: JsonRequired] ControlResponseResult? Result);

public sealed class ControlProtocolException : Exception
{
    public ControlProtocolException(ControlProtocolError error)
        : base(MessageFor(error))
    {
        Error = error;
    }

    public ControlProtocolError Error { get; }

    private static string MessageFor(ControlProtocolError error) => error switch
    {
        ControlProtocolError.InvalidLengthPrefix => "The frame length prefix is invalid.",
        ControlProtocolError.ZeroLengthFrame => "The frame payload is empty.",
        ControlProtocolError.FrameTooLarge => "The frame exceeds the permitted size.",
        ControlProtocolError.TruncatedFrame => "The frame ended before it was complete.",
        ControlProtocolError.InvalidUtf8 => "The frame payload is not valid UTF-8.",
        ControlProtocolError.MalformedJson => "The frame payload is not valid protocol JSON.",
        ControlProtocolError.InvalidContract => "The protocol contract is invalid.",
        ControlProtocolError.UnsupportedVersion => "The protocol version is not supported.",
        ControlProtocolError.UnknownCommand => "The protocol command is not supported.",
        _ => "The protocol input is invalid."
    };
}
