using System.Globalization;
using System.Net;
using System.Net.Sockets;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Windows;

public enum RouteAddressFamily
{
    IPv4 = 2,
    IPv6 = 23
}

public enum RouteMutationAction
{
    Add,
    Delete
}

public sealed record RouteRestorationCommand(
    RouteAddressFamily AddressFamily,
    string DestinationAddress,
    byte PrefixLength,
    string NextHop,
    int InterfaceIndex,
    uint Metric,
    RouteMutationAction Action)
{
    public string Destination => $"{DestinationAddress}/{PrefixLength}";

    public static RouteRestorationCommand FromRequest(AuthorizedRestorationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Category != DryRunOperationCategory.Route)
            throw new InvalidOperationException("Only route requests are supported by the route provider.");

        if (request.TransactionId == Guid.Empty)
            throw new InvalidOperationException("The request transaction identifier is required.");

        if (request.SessionId == Guid.Empty)
            throw new InvalidOperationException("The request session identifier is required.");

        if (string.IsNullOrWhiteSpace(request.OperationIdentity))
            throw new InvalidOperationException("The request operation identity is required.");

        var routeValue = SelectRouteValue(request);
        if (!TryParseRouteValue(routeValue, out var command, out var reason))
            throw new InvalidOperationException(reason);

        var parsedCommand = command ?? throw new InvalidOperationException("The route request could not be parsed into a valid command.");
        return parsedCommand with { Action = DetermineAction(request) };
    }

    public static bool TryParseRouteValue(string routeValue, out RouteRestorationCommand? command, out string reason)
    {
        command = null;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(routeValue))
        {
            reason = "Route value is required.";
            return false;
        }

        var trimmed = routeValue.Trim();
        if (trimmed.StartsWith("destination=", StringComparison.OrdinalIgnoreCase))
        {
            var entries = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var index = entry.IndexOf('=');
                if (index <= 0)
                    continue;

                map[entry[..index]] = entry[(index + 1)..];
            }

            if (!map.TryGetValue("destination", out var destinationText) || string.IsNullOrWhiteSpace(destinationText))
            {
                reason = "Route destination is required.";
                return false;
            }

            if (!TryParseDestination(destinationText, out var destinationAddress, out var prefixLength, out var addressFamily, out var destinationFailure))
            {
                reason = destinationFailure;
                return false;
            }

            if (!map.TryGetValue("nextHop", out var nextHopText) || string.IsNullOrWhiteSpace(nextHopText) || !IPAddress.TryParse(nextHopText, out var nextHopAddress))
            {
                reason = "Route next hop is invalid or missing.";
                return false;
            }

            if (!map.TryGetValue("interfaceIndex", out var interfaceIndexText) || !int.TryParse(interfaceIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interfaceIndex) || interfaceIndex <= 0)
            {
                reason = "Route interface index is required and must be greater than zero.";
                return false;
            }

            if (!map.TryGetValue("metric", out var metricText) || !uint.TryParse(metricText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metric))
            {
                reason = "Route metric is required.";
                return false;
            }

            var nextHopFamily = nextHopAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? RouteAddressFamily.IPv4 : RouteAddressFamily.IPv6;
            if (nextHopFamily != addressFamily)
            {
                reason = "Route destination and next hop address families do not match.";
                return false;
            }

            command = new RouteRestorationCommand(addressFamily, destinationAddress, prefixLength, nextHopText, interfaceIndex, metric, RouteMutationAction.Add);
            return true;
        }

        if (!TryParseDestination(trimmed, out var simpleDestination, out var simplePrefix, out var simpleFamily, out var parseFailure))
        {
            reason = parseFailure;
            return false;
        }

        command = new RouteRestorationCommand(simpleFamily, simpleDestination, simplePrefix, "0.0.0.0", 1, 0, RouteMutationAction.Add);
        return true;
    }

    private static string SelectRouteValue(AuthorizedRestorationRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.OriginalValue) && request.OriginalValue != "<absent>")
            return request.OriginalValue;

        if (!string.IsNullOrWhiteSpace(request.CurrentValue) && request.CurrentValue != "<absent>")
            return request.CurrentValue;

        if (!string.IsNullOrWhiteSpace(request.IntendedRestorationValue) && request.IntendedRestorationValue != "<absent>")
            return request.IntendedRestorationValue;

        throw new InvalidOperationException("The route request requires a route value that identifies a concrete destination.");
    }

    private static RouteMutationAction DetermineAction(AuthorizedRestorationRequest request)
    {
        if (request.OriginalValue == "<absent>" && request.CurrentValue != "<absent>")
            return RouteMutationAction.Delete;

        if (request.CurrentValue == "<absent>" && request.OriginalValue != "<absent>")
            return RouteMutationAction.Add;

        return RouteMutationAction.Add;
    }

    private static bool TryParseDestination(string value, out string destinationAddress, out byte prefixLength, out RouteAddressFamily addressFamily, out string reason)
    {
        destinationAddress = string.Empty;
        prefixLength = 0;
        addressFamily = RouteAddressFamily.IPv4;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Route destination is required.";
            return false;
        }

        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf('/');
        if (separator < 0)
        {
            separator = trimmed.LastIndexOf(':');
        }

        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            reason = "Route destination must include a valid prefix length.";
            return false;
        }

        var addressText = trimmed[..separator];
        var prefixText = trimmed[(separator + 1)..];
        if (!byte.TryParse(prefixText, out var parsedPrefixLength) || parsedPrefixLength > 128)
        {
            reason = "Route prefix length must be between 0 and 128.";
            return false;
        }

        if (!IPAddress.TryParse(addressText, out var address))
        {
            reason = "The route destination is not a valid IP address.";
            return false;
        }

        destinationAddress = address.ToString();
        prefixLength = parsedPrefixLength;
        addressFamily = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? RouteAddressFamily.IPv4 : RouteAddressFamily.IPv6;
        return true;
    }

}

public enum RouteNativeStatus
{
    Success,
    AccessDenied,
    AlreadyExists,
    NotFound,
    InvalidParameter,
    Failed
}

public sealed record RouteQueryResult(bool Exists, RouteState? Route, RouteNativeStatus Status = RouteNativeStatus.Success)
{
    public bool IsExactMatch(RouteRestorationCommand command)
    {
        if (!Exists || Route is null)
            return false;

        var routeFamily = Route.AddressFamily == "2" ? RouteAddressFamily.IPv4 : RouteAddressFamily.IPv6;
        return routeFamily == command.AddressFamily &&
               StringComparer.OrdinalIgnoreCase.Equals(Route.Destination, command.Destination) &&
               StringComparer.Ordinal.Equals(Route.NextHop, command.NextHop) &&
               Route.InterfaceIndex == command.InterfaceIndex &&
               Route.Metric == command.Metric;
    }
}

public interface IRouteMutationNative
{
    Task<RouteQueryResult> QueryAsync(RouteRestorationCommand command, CancellationToken cancellationToken);
    Task<bool> AddRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken);
    Task<bool> DeleteRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken);
}

public sealed class RouteRestorationProvider : IRestorationMutationProvider
{
    private readonly IRouteMutationNative _native;

    public RouteRestorationProvider(IRouteMutationNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public async Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Category != DryRunOperationCategory.Route)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Unsupported, "Only route operations are supported by this provider.", false);

        if (request.TransactionId == Guid.Empty || request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.OperationIdentity))
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, "Route request is incomplete: transaction, session, or operation identity is missing.", false);

        if (!request.AutomaticallyExecutable)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, "The request is not marked as safely executable.", false);

        RouteRestorationCommand command;
        try
        {
            command = RouteRestorationCommand.FromRequest(request);
        }
        catch (InvalidOperationException ex)
        {
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, ex.Message, false);
        }

        var queryResult = await _native.QueryAsync(command, cancellationToken).ConfigureAwait(false);
        if (queryResult.Status != RouteNativeStatus.Success)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, $"Route query failed with native status {queryResult.Status}.", false);

        var isExactMatch = queryResult.Exists && queryResult.IsExactMatch(command);

        if (command.Action == RouteMutationAction.Delete && !queryResult.Exists)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.AlreadyRestored, "The route is already absent from the current state.", true);

        if (command.Action == RouteMutationAction.Add && isExactMatch)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.AlreadyRestored, "The expected route already exists in the current state.", true);

        if (queryResult.Exists && !isExactMatch)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.StateChangedExternally, "The current route state differs from the authorized request assumptions; mutation is blocked.", false);

        var performDelete = command.Action == RouteMutationAction.Delete;
        var success = performDelete
            ? await _native.DeleteRouteAsync(command, cancellationToken).ConfigureAwait(false)
            : await _native.AddRouteAsync(command, cancellationToken).ConfigureAwait(false);

        if (!success)
            return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, performDelete ? "The native route delete operation failed." : "The native route add operation failed.", true);

        return new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Succeeded, performDelete ? "Route removal completed successfully." : "Route restoration add completed successfully.", true);
    }
}
