using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibertyRoute.Service;

internal sealed class ControlCommandDispatcher : IControlCommandDispatcher
{
    private readonly ConnectionController _controller;
    private readonly ILogger<ControlCommandDispatcher> _logger;

    public ControlCommandDispatcher(
        ConnectionController controller,
        ILogger<ControlCommandDispatcher>? logger = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _logger = logger ?? NullLogger<ControlCommandDispatcher>.Instance;
    }

    public async Task<ControlDispatchResult> DispatchAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        return request.Command switch
        {
            ControlCommand.Status => await HandleStatusAsync(caller, request, cancellationToken),
            ControlCommand.Snapshot => await HandleSnapshotAsync(caller, request, cancellationToken),
            ControlCommand.Connect => await HandleConnectAsync(caller, request, cancellationToken),
            ControlCommand.Disconnect => await HandleDisconnectAsync(caller, request, cancellationToken),
            _ => throw new InvalidOperationException("The control command is not supported.")
        };
    }

    private async Task<ControlDispatchResult> HandleStatusAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var authResult = await _controller.GetStatusAuthorizedAsync(caller, cancellationToken);
        LogAuthorizationDecision(request.RequestId, ControlCommand.Status, authResult);

        return authResult.Decision switch
        {
            SessionAuthorizationDecision.NoActiveSession =>
                new ControlDispatchResult(
                    ControlOutcome.Succeeded,
                    ControlErrorCode.None,
                    new ControlStatusResult(ControlConnectionState.Disconnected)),

            SessionAuthorizationDecision.OwnerAuthorized or
            SessionAuthorizationDecision.OperationalOverrideAuthorized =>
                new ControlDispatchResult(
                    ControlOutcome.Succeeded,
                    ControlErrorCode.None,
                    new ControlStatusResult(MapState(authResult.State))),

            SessionAuthorizationDecision.ForeignOwnerDenied or
            SessionAuthorizationDecision.LegacyOwnerDenied or
            SessionAuthorizationDecision.InvalidOwnerDenied =>
                new ControlDispatchResult(
                    ControlOutcome.Failed,
                    ControlErrorCode.ForbiddenCommand,
                    null),

            SessionAuthorizationDecision.InconsistentStateDenied =>
                new ControlDispatchResult(
                    ControlOutcome.Failed,
                    ControlErrorCode.InternalError,
                    null),

            _ => new ControlDispatchResult(
                ControlOutcome.Failed,
                ControlErrorCode.InternalError,
                null)
        };
    }

    private async Task<ControlDispatchResult> HandleSnapshotAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!caller.IsBuiltinAdministrator && !caller.IsLocalSystem)
        {
            _logger.LogWarning(
                "Snapshot denied for request {RequestId}: caller is not an administrator or LocalSystem.",
                request.RequestId);
            return new ControlDispatchResult(
                ControlOutcome.Failed,
                ControlErrorCode.ForbiddenCommand,
                null);
        }

        var snapshot = await _controller.CaptureDiagnosticSnapshotAsync(cancellationToken);
        return new ControlDispatchResult(
            ControlOutcome.Succeeded,
            ControlErrorCode.None,
            new ControlSnapshotResult(MapSnapshot(snapshot)));
    }

    private async Task<ControlDispatchResult> HandleConnectAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var transaction = await _controller.BeginSafeConnectAsync(caller.UserSid, cancellationToken);
        return new ControlDispatchResult(
            ControlOutcome.Succeeded,
            ControlErrorCode.None,
            new ControlConnectResult(MapState(transaction.State)));
    }

    private async Task<ControlDispatchResult> HandleDisconnectAsync(
        ControlCallerIdentity caller,
        ControlRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var authResult = await _controller.RollbackAuthorizedAsync(
            caller,
            "User requested disconnect.",
            cancellationToken);

        LogAuthorizationDecision(request.RequestId, ControlCommand.Disconnect, authResult);

        return authResult.Decision switch
        {
            SessionAuthorizationDecision.NoActiveSession or
            SessionAuthorizationDecision.OwnerAuthorized or
            SessionAuthorizationDecision.OperationalOverrideAuthorized =>
                new ControlDispatchResult(
                    ControlOutcome.Succeeded,
                    ControlErrorCode.None,
                    new ControlDisconnectResult(ControlConnectionState.Disconnected)),

            SessionAuthorizationDecision.ForeignOwnerDenied or
            SessionAuthorizationDecision.LegacyOwnerDenied or
            SessionAuthorizationDecision.InvalidOwnerDenied =>
                new ControlDispatchResult(
                    ControlOutcome.Failed,
                    ControlErrorCode.ForbiddenCommand,
                    null),

            SessionAuthorizationDecision.InconsistentStateDenied =>
                new ControlDispatchResult(
                    ControlOutcome.Failed,
                    ControlErrorCode.InternalError,
                    null),

            _ => new ControlDispatchResult(
                ControlOutcome.Failed,
                ControlErrorCode.InternalError,
                null)
        };
    }

    private void LogAuthorizationDecision(
        Guid requestId,
        ControlCommand command,
        SessionAuthorizationResult authResult)
    {
        switch (authResult.Decision)
        {
            case SessionAuthorizationDecision.OwnerAuthorized:
                _logger.LogInformation(
                    "Request {RequestId} command {Command} authorized for session {SessionId}: OwnerAuthorized.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.OperationalOverrideAuthorized:
                _logger.LogInformation(
                    "Request {RequestId} command {Command} authorized for session {SessionId}: OperationalOverrideAuthorized.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.ForeignOwnerDenied:
                _logger.LogWarning(
                    "Request {RequestId} command {Command} denied for session {SessionId}: ForeignOwnerDenied.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.LegacyOwnerDenied:
                _logger.LogWarning(
                    "Request {RequestId} command {Command} denied for session {SessionId}: LegacyOwnerDenied.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.InvalidOwnerDenied:
                _logger.LogWarning(
                    "Request {RequestId} command {Command} denied for session {SessionId}: InvalidOwnerDenied.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.InconsistentStateDenied:
                _logger.LogError(
                    "Request {RequestId} command {Command} denied for session {SessionId}: InconsistentStateDenied.",
                    requestId,
                    command,
                    authResult.SessionId);
                break;

            case SessionAuthorizationDecision.NoActiveSession:
                _logger.LogDebug(
                    "Request {RequestId} command {Command}: NoActiveSession.",
                    requestId,
                    command);
                break;
        }
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
        return new ControlNetworkSnapshot(
            snapshot.CapturedAtUtc,
            Required(snapshot.MachineName, nameof(snapshot.MachineName)),
            RequiredItems(snapshot.Adapters, nameof(snapshot.Adapters)).Select(MapAdapter).ToArray(),
            snapshot.Routes?.Select(MapRoute).ToArray(),
            snapshot.DnsInterfaces?.Select(MapDnsInterface).ToArray());
    }

    private static ControlAdapterState MapAdapter(AdapterState adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return new ControlAdapterState(
            Required(adapter.Id, nameof(adapter.Id)),
            Required(adapter.Name, nameof(adapter.Name)),
            Required(adapter.Description, nameof(adapter.Description)),
            Required(adapter.NetworkInterfaceType, nameof(adapter.NetworkInterfaceType)),
            Required(adapter.OperationalStatus, nameof(adapter.OperationalStatus)),
            RequiredStrings(adapter.UnicastAddresses, nameof(adapter.UnicastAddresses)),
            RequiredStrings(adapter.Gateways, nameof(adapter.Gateways)),
            RequiredStrings(adapter.DnsServers, nameof(adapter.DnsServers)));
    }

    private static ControlRouteState MapRoute(RouteState route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new ControlRouteState(
            Required(route.Destination, nameof(route.Destination)),
            Required(route.NextHop, nameof(route.NextHop)),
            route.InterfaceIndex,
            route.Metric,
            Required(route.AddressFamily, nameof(route.AddressFamily)));
    }

    private static ControlDnsInterfaceState MapDnsInterface(DnsInterfaceState dns)
    {
        ArgumentNullException.ThrowIfNull(dns);
        return new ControlDnsInterfaceState(
            Required(dns.InterfaceId, nameof(dns.InterfaceId)),
            Required(dns.InterfaceName, nameof(dns.InterfaceName)),
            dns.IsUp,
            RequiredStrings(dns.DnsServers, nameof(dns.DnsServers)),
            NullableStrings(dns.IPv4DnsServers, nameof(dns.IPv4DnsServers)),
            NullableStrings(dns.IPv6DnsServers, nameof(dns.IPv6DnsServers)),
            MapDnsSource(dns.IPv4ConfigurationSource),
            MapDnsSource(dns.IPv6ConfigurationSource),
            NullableStrings(dns.IPv4StaticDnsServers, nameof(dns.IPv4StaticDnsServers)),
            NullableStrings(dns.IPv4DhcpDnsServers, nameof(dns.IPv4DhcpDnsServers)),
            NullableStrings(dns.IPv6StaticDnsServers, nameof(dns.IPv6StaticDnsServers)),
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
