using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace LibertyRoute.Core;

public enum NativeRouteAddressFamily : ushort
{
    IPv4 = 2,
    IPv6 = 23
}

public sealed class NativeRouteKey : IEquatable<NativeRouteKey>
{
    [JsonConstructor]
    public NativeRouteKey(
        NativeRouteAddressFamily addressFamily,
        string destinationAddress,
        byte destinationPrefixLength,
        string nextHopAddress,
        uint nextHopScopeId,
        ulong interfaceLuid)
    {
        if (addressFamily is not (NativeRouteAddressFamily.IPv4 or NativeRouteAddressFamily.IPv6))
            throw new ArgumentOutOfRangeException(nameof(addressFamily));
        if (interfaceLuid == 0)
            throw new ArgumentOutOfRangeException(nameof(interfaceLuid));

        var byteLength = addressFamily == NativeRouteAddressFamily.IPv4 ? 4 : 16;
        var destination = ParseCanonicalHex(destinationAddress, byteLength, nameof(destinationAddress));
        var nextHop = ParseCanonicalHex(nextHopAddress, byteLength, nameof(nextHopAddress));
        var maximumPrefix = addressFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        if (destinationPrefixLength > maximumPrefix)
            throw new ArgumentOutOfRangeException(nameof(destinationPrefixLength));
        if (!IsCanonicalNetwork(destination, destinationPrefixLength))
            throw new ArgumentException("Destination host bits must already be zero for its prefix.", nameof(destinationAddress));

        ValidateScope(addressFamily, nextHop, nextHopScopeId);
        AddressFamily = addressFamily;
        DestinationAddress = destinationAddress;
        DestinationPrefixLength = destinationPrefixLength;
        NextHopAddress = nextHopAddress;
        NextHopScopeId = nextHopScopeId;
        InterfaceLuid = interfaceLuid;
    }

    public NativeRouteAddressFamily AddressFamily { get; }
    public string DestinationAddress { get; }
    public byte DestinationPrefixLength { get; }
    public string NextHopAddress { get; }
    public uint NextHopScopeId { get; }
    public ulong InterfaceLuid { get; }

    public byte[] GetDestinationAddressBytes() => Convert.FromHexString(DestinationAddress);
    public byte[] GetNextHopAddressBytes() => Convert.FromHexString(NextHopAddress);

    public static NativeRouteKey Create(
        NativeRouteAddressFamily family,
        IPAddress destination,
        byte prefixLength,
        IPAddress nextHop,
        ulong interfaceLuid)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(nextHop);
        var expected = family switch
        {
            NativeRouteAddressFamily.IPv4 => System.Net.Sockets.AddressFamily.InterNetwork,
            NativeRouteAddressFamily.IPv6 => System.Net.Sockets.AddressFamily.InterNetworkV6,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
        if (destination.AddressFamily != expected || nextHop.AddressFamily != expected)
            throw new ArgumentException("Route addresses do not match the declared native family.");
        if (family == NativeRouteAddressFamily.IPv6 && destination.ScopeId != 0)
            throw new ArgumentException("A route destination must not carry an IPv6 scope id.", nameof(destination));

        return new NativeRouteKey(
            family,
            Convert.ToHexString(destination.GetAddressBytes()),
            prefixLength,
            Convert.ToHexString(nextHop.GetAddressBytes()),
            family == NativeRouteAddressFamily.IPv6 ? checked((uint)nextHop.ScopeId) : 0,
            interfaceLuid);
    }

    public bool HasSameReducedIdentity(NativeRouteKey other) =>
        other is not null && other.AddressFamily == AddressFamily &&
        StringComparer.Ordinal.Equals(other.DestinationAddress, DestinationAddress) &&
        other.DestinationPrefixLength == DestinationPrefixLength;

    public bool Equals(NativeRouteKey? other) => other is not null &&
        AddressFamily == other.AddressFamily &&
        StringComparer.Ordinal.Equals(DestinationAddress, other.DestinationAddress) &&
        DestinationPrefixLength == other.DestinationPrefixLength &&
        StringComparer.Ordinal.Equals(NextHopAddress, other.NextHopAddress) &&
        NextHopScopeId == other.NextHopScopeId && InterfaceLuid == other.InterfaceLuid;

    public override bool Equals(object? obj) => Equals(obj as NativeRouteKey);
    public override int GetHashCode() => HashCode.Combine(AddressFamily, DestinationAddress,
        DestinationPrefixLength, NextHopAddress, NextHopScopeId, InterfaceLuid);

    private static byte[] ParseCanonicalHex(string value, int byteLength, string parameterName)
    {
        if (value is null || value.Length != byteLength * 2 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException($"Address must be exactly {byteLength * 2} uppercase hexadecimal characters.", parameterName);
        return Convert.FromHexString(value);
    }

    private static bool IsCanonicalNetwork(byte[] address, int prefixLength)
    {
        var completeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits != 0 && (address[completeBytes] & (byte)(0xff >> remainingBits)) != 0)
            return false;
        var firstHostByte = completeBytes + (remainingBits == 0 ? 0 : 1);
        return address.AsSpan(firstHostByte).IndexOfAnyExcept((byte)0) < 0;
    }

    private static void ValidateScope(NativeRouteAddressFamily family, byte[] nextHop, uint scope)
    {
        if (family == NativeRouteAddressFamily.IPv4)
        {
            if (scope != 0)
                throw new ArgumentOutOfRangeException(nameof(scope), "IPv4 next-hop scope must be zero.");
            return;
        }

        var unspecified = nextHop.All(value => value == 0);
        var linkLocal = nextHop[0] == 0xfe && (nextHop[1] & 0xc0) == 0x80;
        var multicast = nextHop[0] == 0xff;
        if (multicast)
            throw new ArgumentException("IPv6 multicast next hops are not supported by schema v1.", nameof(nextHop));
        if (unspecified && scope != 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "An unspecified IPv6 next hop requires scope zero.");
        if (linkLocal && scope == 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "An IPv6 link-local next hop requires a nonzero scope.");
        if (!unspecified && !linkLocal && scope != 0)
            throw new ArgumentOutOfRangeException(nameof(scope), "A non-link-local IPv6 next hop requires scope zero in schema v1.");
    }
}

public sealed class NativeRouteProfile : IEquatable<NativeRouteProfile>
{
    [JsonConstructor]
    public NativeRouteProfile(byte sitePrefixLength, uint initialValidLifetime,
        uint initialPreferredLifetime, uint metric, uint protocol, bool loopback,
        bool autoconfigureAddress, bool publish, bool immortal)
    {
        if (initialPreferredLifetime > initialValidLifetime)
            throw new ArgumentOutOfRangeException(nameof(initialPreferredLifetime));
        if (protocol != 3)
            throw new ArgumentOutOfRangeException(nameof(protocol), "Schema v1 requires MIB_IPPROTO_NETMGMT (3).");
        SitePrefixLength = sitePrefixLength;
        InitialValidLifetime = initialValidLifetime;
        InitialPreferredLifetime = initialPreferredLifetime;
        Metric = metric;
        Protocol = protocol;
        Loopback = loopback;
        AutoconfigureAddress = autoconfigureAddress;
        Publish = publish;
        Immortal = immortal;
    }

    public byte SitePrefixLength { get; }
    public uint InitialValidLifetime { get; }
    public uint InitialPreferredLifetime { get; }
    public uint Metric { get; }
    public uint Protocol { get; }
    public bool Loopback { get; }
    public bool AutoconfigureAddress { get; }
    public bool Publish { get; }
    public bool Immortal { get; }

    public void ValidateFor(NativeRouteKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var maximum = key.AddressFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        if (SitePrefixLength > maximum)
            throw new InvalidOperationException("Site prefix length is incompatible with the route family.");
    }

    public bool IsSatisfiedBy(NativeRouteProfile observed) => observed is not null &&
        SitePrefixLength == observed.SitePrefixLength &&
        LifetimeMatches(InitialValidLifetime, observed.InitialValidLifetime) &&
        LifetimeMatches(InitialPreferredLifetime, observed.InitialPreferredLifetime) &&
        Metric == observed.Metric && Protocol == observed.Protocol &&
        Loopback == observed.Loopback && AutoconfigureAddress == observed.AutoconfigureAddress &&
        Publish == observed.Publish && Immortal == observed.Immortal;

    public bool Equals(NativeRouteProfile? other) => other is not null &&
        SitePrefixLength == other.SitePrefixLength &&
        InitialValidLifetime == other.InitialValidLifetime &&
        InitialPreferredLifetime == other.InitialPreferredLifetime && Metric == other.Metric &&
        Protocol == other.Protocol && Loopback == other.Loopback &&
        AutoconfigureAddress == other.AutoconfigureAddress && Publish == other.Publish && Immortal == other.Immortal;
    public override bool Equals(object? obj) => Equals(obj as NativeRouteProfile);
    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(SitePrefixLength,
        InitialValidLifetime, InitialPreferredLifetime, Metric, Protocol),
        Loopback, AutoconfigureAddress, Publish, Immortal);

    private static bool LifetimeMatches(uint expected, uint actual) =>
        expected == uint.MaxValue ? actual == uint.MaxValue : actual <= expected;
}

public sealed class ExactRouteMutationIdentity : IEquatable<ExactRouteMutationIdentity>
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public ExactRouteMutationIdentity(int schemaVersion, NativeRouteKey key, NativeRouteProfile profile)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Only exact-route schema version 1 is supported.");
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Profile.ValidateFor(Key);
        SchemaVersion = schemaVersion;
    }

    public int SchemaVersion { get; }
    public NativeRouteKey Key { get; }
    public NativeRouteProfile Profile { get; }

    public bool Equals(ExactRouteMutationIdentity? other) => other is not null &&
        SchemaVersion == other.SchemaVersion && Key.Equals(other.Key) && Profile.Equals(other.Profile);
    public override bool Equals(object? obj) => Equals(obj as ExactRouteMutationIdentity);
    public override int GetHashCode() => HashCode.Combine(SchemaVersion, Key, Profile);
}
