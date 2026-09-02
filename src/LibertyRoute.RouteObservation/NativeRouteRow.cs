using System.Net;
using System.Net.Sockets;

namespace LibertyRoute.RouteObservation;

public enum NativeRouteAddressFamily : ushort
{
    IPv4 = 2,
    IPv6 = 23
}

public sealed record NativeRouteKey(
    NativeRouteAddressFamily AddressFamily,
    string DestinationAddress,
    byte DestinationPrefixLength,
    string NextHopAddress,
    long NextHopScopeId,
    ulong InterfaceLuid)
{
    public static NativeRouteKey Create(
        NativeRouteAddressFamily family,
        IPAddress destination,
        byte prefixLength,
        IPAddress nextHop,
        ulong interfaceLuid)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(nextHop);
        if (family is not (NativeRouteAddressFamily.IPv4 or NativeRouteAddressFamily.IPv6))
            throw new ArgumentOutOfRangeException(nameof(family));
        if (interfaceLuid == 0) throw new ArgumentOutOfRangeException(nameof(interfaceLuid));

        var expected = family == NativeRouteAddressFamily.IPv4
            ? System.Net.Sockets.AddressFamily.InterNetwork
            : System.Net.Sockets.AddressFamily.InterNetworkV6;
        if (destination.AddressFamily != expected || nextHop.AddressFamily != expected)
            throw new ArgumentException("Route addresses do not match the declared native family.");
        var maximumPrefix = family == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        if (prefixLength > maximumPrefix)
            throw new ArgumentOutOfRangeException(nameof(prefixLength));

        var canonicalDestination = new IPAddress(destination.GetAddressBytes());
        var canonicalNextHop = new IPAddress(nextHop.GetAddressBytes());
        return new NativeRouteKey(family, canonicalDestination.ToString(), prefixLength,
            canonicalNextHop.ToString(), family == NativeRouteAddressFamily.IPv6 ? nextHop.ScopeId : 0,
            interfaceLuid);
    }

    public bool HasSameReducedIdentity(ExactNativeRouteRow row) =>
        row.Key.AddressFamily == AddressFamily &&
        StringComparer.Ordinal.Equals(row.Key.DestinationAddress, DestinationAddress) &&
        row.Key.DestinationPrefixLength == DestinationPrefixLength;
}

public sealed record ExactNativeRouteRow(
    NativeRouteKey Key,
    uint InterfaceIndex,
    byte SitePrefixLength,
    uint ValidLifetime,
    uint PreferredLifetime,
    uint Metric,
    uint Protocol,
    bool Loopback,
    bool AutoconfigureAddress,
    bool Publish,
    bool Immortal,
    uint Age,
    uint Origin);

public sealed record NativeLifetimeExpectation(uint InitialValue)
{
    public bool Matches(uint actual) => InitialValue == uint.MaxValue
        ? actual == uint.MaxValue
        : actual <= InitialValue;
}

public sealed record NativeRouteExpectedProfile(
    uint InterfaceIndex,
    byte SitePrefixLength,
    NativeLifetimeExpectation ValidLifetime,
    NativeLifetimeExpectation PreferredLifetime,
    uint Metric,
    uint Protocol,
    bool Loopback,
    bool AutoconfigureAddress,
    bool Publish,
    bool Immortal)
{
    public bool IsValidFor(NativeRouteKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var maximumPrefix = key.AddressFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        return InterfaceIndex != 0 && SitePrefixLength <= maximumPrefix &&
            SitePrefixLength <= key.DestinationPrefixLength &&
            PreferredLifetime.InitialValue <= ValidLifetime.InitialValue;
    }

    public bool Matches(ExactNativeRouteRow row) =>
        row.InterfaceIndex == InterfaceIndex &&
        row.SitePrefixLength == SitePrefixLength &&
        ValidLifetime.Matches(row.ValidLifetime) &&
        PreferredLifetime.Matches(row.PreferredLifetime) &&
        row.Metric == Metric &&
        row.Protocol == Protocol &&
        row.Loopback == Loopback &&
        row.AutoconfigureAddress == AutoconfigureAddress &&
        row.Publish == Publish &&
        row.Immortal == Immortal;
}

public sealed record ExactRouteObservation(
    IReadOnlyList<ExactNativeRouteRow> Rows,
    bool Ipv4Complete,
    bool Ipv6Complete,
    bool Truncated,
    IReadOnlyList<string> IncompleteReasons)
{
    public bool Complete => Ipv4Complete && Ipv6Complete && !Truncated && IncompleteReasons.Count == 0;
}

internal static class NativeRouteEvidenceValidator
{
    public static bool IsValidKey(NativeRouteKey key)
    {
        if (key.AddressFamily is not (NativeRouteAddressFamily.IPv4 or NativeRouteAddressFamily.IPv6) ||
            key.InterfaceLuid == 0 || key.NextHopScopeId < 0 || key.NextHopScopeId > uint.MaxValue)
            return false;
        if (!IPAddress.TryParse(key.DestinationAddress, out var destination) ||
            !IPAddress.TryParse(key.NextHopAddress, out var nextHop))
            return false;
        var expected = key.AddressFamily == NativeRouteAddressFamily.IPv4
            ? System.Net.Sockets.AddressFamily.InterNetwork
            : System.Net.Sockets.AddressFamily.InterNetworkV6;
        var maximumPrefix = key.AddressFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        return destination.AddressFamily == expected && nextHop.AddressFamily == expected &&
            key.DestinationPrefixLength <= maximumPrefix &&
            StringComparer.Ordinal.Equals(new IPAddress(destination.GetAddressBytes()).ToString(), key.DestinationAddress) &&
            StringComparer.Ordinal.Equals(new IPAddress(nextHop.GetAddressBytes()).ToString(), key.NextHopAddress) &&
            (key.AddressFamily == NativeRouteAddressFamily.IPv6 || key.NextHopScopeId == 0);
    }

    public static bool IsValid(ExactNativeRouteRow row)
    {
        if (!IsValidKey(row.Key) || row.InterfaceIndex == 0 ||
            row.PreferredLifetime > row.ValidLifetime)
            return false;
        var maximumPrefix = row.Key.AddressFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        return row.SitePrefixLength <= maximumPrefix && row.SitePrefixLength <= row.Key.DestinationPrefixLength;
    }
}
