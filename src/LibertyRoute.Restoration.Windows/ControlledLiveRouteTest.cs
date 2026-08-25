using System.Net;
using LibertyRoute.Core;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

public enum LiveRouteTestEligibilityStatus
{
    Eligible,
    InvalidCapability,
    BlockedByAuthorization,
    SessionMismatch,
    InvalidBatch,
    ExistingRoute,
    UnsafeDestination,
    UnknownInterface,
    UnsafeInterface,
    UnsafeNextHop
}

public sealed record LiveRouteTestEligibility(
    LiveRouteTestEligibilityStatus Status,
    string Reason,
    RouteRestorationCommand? Command)
{
    public bool IsEligible => Status == LiveRouteTestEligibilityStatus.Eligible;
}

internal sealed record LiveRouteTestContext(
    IReadOnlyList<RouteState> CurrentRoutes,
    IReadOnlyList<RouteState> DefaultRoutes,
    IReadOnlyList<string> GatewayAddresses,
    IReadOnlyList<string> DnsServerAddresses,
    IReadOnlyList<string> LocalPrefixes,
    IReadOnlySet<int> KnownInterfaceIndexes,
    IReadOnlySet<int> UnsafeInterfaceIndexes);

internal static class ControlledLiveRouteTestValidator
{
    private static readonly string[] TestNetPrefixes =
    {
        "192.0.2.0/24",
        "198.51.100.0/24",
        "203.0.113.0/24"
    };

    public static LiveRouteTestEligibility Validate(
        RestorationExecutionPreparation preparation,
        Guid activeSessionId,
        RestorationExecutionCapability? capability,
        LiveRouteTestContext context)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(context);

        if (capability is null || !capability.IsValid)
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.InvalidCapability, "A valid controlled live-test capability is required.", null);

        if (!preparation.CanExecuteAutomatically)
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.BlockedByAuthorization, "The prepared batch is not fully authorized for automatic execution.", null);

        if (preparation.AuthorizedRequests.Count != 1)
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.InvalidBatch, "The controlled live route test requires exactly one authorized request.", null);

        var request = preparation.AuthorizedRequests[0];
        if (request.SessionId != activeSessionId)
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.SessionMismatch, "The authorized request does not match the active test session.", null);

        if (request.Category != DryRunOperationCategory.Route || request.Action != DryRunAction.RestoreBaseline)
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.BlockedByAuthorization, "Only one authorized baseline route restoration is permitted.", null);

        RouteRestorationCommand command;
        try
        {
            command = RouteRestorationCommand.FromRequest(request);
        }
        catch (InvalidOperationException exception)
        {
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.BlockedByAuthorization, exception.Message, null);
        }

        if (command.PrefixLength == 0 || command.Destination is "0.0.0.0/0" or "::/0")
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeDestination, "Default routes are prohibited by the controlled live-test gate.", null);

        if (!TestNetPrefixes.Contains(command.Destination, StringComparer.Ordinal))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeDestination, "The destination must be one of the reserved TEST-NET prefixes.", null);

        if (context.CurrentRoutes.Any(route => StringComparer.OrdinalIgnoreCase.Equals(CanonicalPrefix(route.Destination), command.Destination)))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.ExistingRoute, "The exact TEST-NET destination already exists in the current route state.", null);

        if (!context.KnownInterfaceIndexes.Contains(command.InterfaceIndex))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnknownInterface, "The proposed interface index is not present in the read-only interface inventory.", null);

        if (context.UnsafeInterfaceIndexes.Contains(command.InterfaceIndex))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeInterface, "The proposed interface is marked unsafe for a live route test.", null);

        if (context.DefaultRoutes.Any(route => route.InterfaceIndex == command.InterfaceIndex))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeInterface, "The proposed interface owns an active default route.", null);

        if (context.GatewayAddresses.Any(address => StringComparer.OrdinalIgnoreCase.Equals(address, command.NextHop)) ||
            context.DnsServerAddresses.Any(address => StringComparer.OrdinalIgnoreCase.Equals(address, command.NextHop)))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeNextHop, "The proposed next hop targets a gateway or DNS server.", null);

        if (context.LocalPrefixes.Any(prefix => PrefixContains(prefix, command.Destination)))
            return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.UnsafeDestination, "The proposed destination overlaps a current local subnet.", null);

        return new LiveRouteTestEligibility(LiveRouteTestEligibilityStatus.Eligible, "The single authorized route targets an absent reserved TEST-NET prefix on a known non-default interface.", command);
    }

    private static string CanonicalPrefix(string value)
    {
        var separator = value.LastIndexOf('/');
        if (separator <= 0 || !IPAddress.TryParse(value[..separator], out var address) || !byte.TryParse(value[(separator + 1)..], out var prefix))
            return value;

        return $"{address}/{prefix}";
    }

    private static bool PrefixContains(string prefixText, string destinationText)
    {
        var prefixSeparator = prefixText.LastIndexOf('/');
        var destinationSeparator = destinationText.LastIndexOf('/');
        if (prefixSeparator <= 0 || destinationSeparator <= 0 ||
            !IPAddress.TryParse(prefixText[..prefixSeparator], out var prefixAddress) ||
            !IPAddress.TryParse(destinationText[..destinationSeparator], out var destinationAddress) ||
            !byte.TryParse(prefixText[(prefixSeparator + 1)..], out var prefixLength))
            return false;

        if (prefixAddress.AddressFamily != destinationAddress.AddressFamily)
            return false;

        var prefixBytes = prefixAddress.GetAddressBytes();
        var destinationBytes = destinationAddress.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (prefixBytes[index] != destinationBytes[index])
                return false;
        }

        return remainingBits == 0 || (prefixBytes[fullBytes] & (0xff << (8 - remainingBits))) == (destinationBytes[fullBytes] & (0xff << (8 - remainingBits)));
    }
}
