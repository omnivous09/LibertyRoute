using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Tests;

public sealed class ExactRouteMutationIdentityTests
{
    [Theory]
    [InlineData("192.0.2.9", 32, "C0000209")]
    [InlineData("192.0.2.0", 24, "C0000200")]
    public void CanonicalIpv4KeysAreAccepted(string destination, byte prefix, string expectedHex)
    {
        var key = Key(NativeRouteAddressFamily.IPv4, destination, prefix, "0.0.0.0");
        Assert.Equal(expectedHex, key.DestinationAddress);
        Assert.Equal("00000000", key.NextHopAddress);
    }

    [Fact]
    public void Ipv4DestinationHostBitsAreRejected() =>
        Assert.Throws<ArgumentException>(() => Key(NativeRouteAddressFamily.IPv4, "192.0.2.9", 24, "0.0.0.0"));

    [Theory]
    [InlineData("2001:db8::1", 128)]
    [InlineData("2001:db8::", 64)]
    public void CanonicalIpv6KeysAreAccepted(string destination, byte prefix)
    {
        var key = Key(NativeRouteAddressFamily.IPv6, destination, prefix, "::");
        Assert.Equal(32, key.DestinationAddress.Length);
    }

    [Fact]
    public void Ipv6DestinationHostBitsAreRejected() =>
        Assert.Throws<ArgumentException>(() => Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 64, "::"));

    [Theory]
    [InlineData("c0000201")]
    [InlineData(" C0000201")]
    [InlineData("C0:00:02:01")]
    [InlineData("0xC0000201")]
    [InlineData("C00002G1")]
    [InlineData("C00002")]
    public void NoncanonicalSerializedAddressesAreRejected(string address) =>
        Assert.Throws<ArgumentException>(() => new NativeRouteKey(NativeRouteAddressFamily.IPv4,
            address, 32, "00000000", 0, 1));

    [Fact]
    public void WrongFamilyAndAddressLengthAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRouteKey((NativeRouteAddressFamily)99,
            "C0000201", 32, "00000000", 0, 1));
        Assert.Throws<ArgumentException>(() => new NativeRouteKey(NativeRouteAddressFamily.IPv6,
            "C0000201", 128, new string('0', 32), 0, 1));
    }

    [Fact]
    public void Ipv4ScopeAndZeroLuidAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRouteKey(NativeRouteAddressFamily.IPv4,
            "C0000201", 32, "00000000", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRouteKey(NativeRouteAddressFamily.IPv4,
            "C0000201", 32, "00000000", 0, 0));
    }

    [Fact]
    public void Ipv6ScopeRulesAreFailClosed()
    {
        Assert.NotNull(Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "::"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "::", 1));
        Assert.NotNull(Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1", 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1"));
        Assert.NotNull(Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "2001:db8::2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "2001:db8::2", 7));
        Assert.Throws<ArgumentException>(() => Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "ff02::1", 7));
        Assert.Equal(uint.MaxValue,
            Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1", uint.MaxValue).NextHopScopeId);
    }

    [Theory]
    [InlineData(100U, 100U)]
    [InlineData(100U, 50U)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void ValidProfilesRoundTrip(uint valid, uint preferred)
    {
        var identity = Identity(Profile(valid, preferred));
        var json = JsonSerializer.Serialize(identity);
        Assert.Equal(identity, JsonSerializer.Deserialize<ExactRouteMutationIdentity>(json));
    }

    [Fact]
    public void InvalidLifetimeProtocolAndSitePrefixAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Profile(50, 51));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeRouteProfile(32, 1, 1, 5, 2, false, false, false, false));
        Assert.Throws<InvalidOperationException>(() => new ExactRouteMutationIdentity(1,
            Key(NativeRouteAddressFamily.IPv4, "192.0.2.1", 32, "0.0.0.0"),
            new NativeRouteProfile(33, 1, 1, 5, 3, false, false, false, false)));
    }

    [Fact]
    public void FiniteAndInfiniteLifetimeMatchingRemainDistinctFromEquality()
    {
        var expected = Profile(100, 80);
        var decayed = Profile(90, 70);
        Assert.NotEqual(expected, decayed);
        Assert.True(expected.IsSatisfiedBy(decayed));
        Assert.False(expected.IsSatisfiedBy(Profile(101, 70)));
        Assert.True(Profile(uint.MaxValue, uint.MaxValue).IsSatisfiedBy(Profile(uint.MaxValue, uint.MaxValue)));
        Assert.False(Profile(uint.MaxValue, uint.MaxValue).IsSatisfiedBy(Profile(100, 100)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedVersionsAreRejected(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExactRouteMutationIdentity(version,
            Key(NativeRouteAddressFamily.IPv4, "192.0.2.1", 32, "0.0.0.0"), Profile(1, 1)));

    [Theory]
    [InlineData("{\"key\":null,\"profile\":null}")]
    [InlineData("{\"schemaVersion\":1,\"profile\":null}")]
    [InlineData("{\"schemaVersion\":1,\"key\":null}")]
    [InlineData("{\"schemaVersion\":2,\"key\":null,\"profile\":null}")]
    public void MalformedDeserializationCannotBypassValidation(string json) =>
        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize<ExactRouteMutationIdentity>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));

    [Theory]
    [MemberData(nameof(CompleteMalformedPayloads))]
    public void CompleteMalformedJsonCannotBypassNestedValidation(string description, string json)
    {
        var exception = Record.Exception(() => JsonSerializer.Deserialize<ExactRouteMutationIdentity>(json));
        Assert.True(exception is JsonException or ArgumentException or InvalidOperationException,
            $"{description} unexpectedly produced {exception?.GetType().Name ?? "no exception"}.");
    }

    [Theory]
    [MemberData(nameof(CompleteValidPayloads))]
    public void CompleteValidJsonRoundTripsThroughValidatedConstructors(ExactRouteMutationIdentity expected)
    {
        var json = JsonSerializer.Serialize(expected);
        Assert.Equal(expected, JsonSerializer.Deserialize<ExactRouteMutationIdentity>(json));
    }

    public static IEnumerable<object[]> CompleteMalformedPayloads()
    {
        yield return Case("missing schema", Ipv4(json => json.Remove("SchemaVersion")));
        yield return Case("zero schema", Ipv4(json => json["SchemaVersion"] = 0));
        yield return Case("negative schema", Ipv4(json => json["SchemaVersion"] = -1));
        yield return Case("higher schema", Ipv4(json => json["SchemaVersion"] = 2));
        yield return Case("maximum schema", Ipv4(json => json["SchemaVersion"] = int.MaxValue));
        yield return Case("wrong-type schema", Ipv4(json => json["SchemaVersion"] = "1"));
        yield return Case("missing key", Ipv4(json => json.Remove("Key")));
        yield return Case("null key", Ipv4(json => json["Key"] = null));
        yield return Case("missing profile", Ipv4(json => json.Remove("Profile")));
        yield return Case("null profile", Ipv4(json => json["Profile"] = null));

        foreach (var value in new[] { "c0000201", "C00002a1", " C0000201", "C000 201", "192.0.2.1", "C0:00:02:01", "0xC0000201", "C00002G1", "C00002" })
            yield return Case($"destination {value}", Ipv4(json => KeyNode(json)["DestinationAddress"] = value));
        yield return Case("noncanonical destination", Ipv4(json =>
        {
            KeyNode(json)["DestinationAddress"] = "C0000209";
            KeyNode(json)["DestinationPrefixLength"] = 24;
        }));
        yield return Case("IPv4 nonzero scope", Ipv4(json => KeyNode(json)["NextHopScopeId"] = 1));
        yield return Case("zero LUID", Ipv4(json => KeyNode(json)["InterfaceLuid"] = 0));
        yield return Case("invalid protocol", Ipv4(json => ProfileNode(json)["Protocol"] = 2));
        yield return Case("preferred exceeds valid", Ipv4(json =>
        {
            ProfileNode(json)["InitialValidLifetime"] = 10;
            ProfileNode(json)["InitialPreferredLifetime"] = 11;
        }));
        yield return Case("site prefix outside family", Ipv4(json => ProfileNode(json)["SitePrefixLength"] = 33));

        yield return Case("unspecified IPv6 scope", Ipv6(json =>
        {
            KeyNode(json)["NextHopAddress"] = new string('0', 32);
            KeyNode(json)["NextHopScopeId"] = 1;
        }));
        yield return Case("link-local IPv6 zero scope", Ipv6(json => KeyNode(json)["NextHopScopeId"] = 0));
        yield return Case("global IPv6 nonzero scope", Ipv6(json =>
        {
            KeyNode(json)["NextHopAddress"] = "20010DB8000000000000000000000002";
            KeyNode(json)["NextHopScopeId"] = 1;
        }));
        yield return Case("multicast IPv6", Ipv6(json => KeyNode(json)["NextHopAddress"] = "FF020000000000000000000000000001"));
    }

    public static IEnumerable<object[]> CompleteValidPayloads()
    {
        yield return new object[] { Identity(Profile(100, 80)) };
        var key = Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1", 7);
        yield return new object[] { new ExactRouteMutationIdentity(1, key,
            new NativeRouteProfile(128, uint.MaxValue, uint.MaxValue, 5, 3, false, false, false, true)) };
    }

    [Fact]
    public void AddressCopiesCannotMutateIdentityAndEqualityIsValueBased()
    {
        var first = Key(NativeRouteAddressFamily.IPv4, "192.0.2.1", 32, "0.0.0.0");
        var equal = Key(NativeRouteAddressFamily.IPv4, "192.0.2.1", 32, "0.0.0.0");
        var hash = first.GetHashCode();
        var destination = first.GetDestinationAddressBytes();
        var nextHop = first.GetNextHopAddressBytes();
        destination[0] = 0;
        nextHop[0] = 255;
        Assert.Equal(equal, first);
        Assert.Equal(hash, first.GetHashCode());
        Assert.Equal("C0000201", first.DestinationAddress);
        Assert.Equal("00000000", first.NextHopAddress);
    }

    private static NativeRouteKey Key(NativeRouteAddressFamily family, string destination, byte prefix,
        string nextHop, uint scope = 0) => NativeRouteKey.Create(family, IPAddress.Parse(destination), prefix,
            family == NativeRouteAddressFamily.IPv6
                ? new IPAddress(IPAddress.Parse(nextHop).GetAddressBytes(), scope)
                : IPAddress.Parse(nextHop), 42);

    private static NativeRouteProfile Profile(uint valid, uint preferred) =>
        new(32, valid, preferred, 5, 3, false, false, false, false);

    private static ExactRouteMutationIdentity Identity(NativeRouteProfile profile) => new(1,
        Key(NativeRouteAddressFamily.IPv4, "192.0.2.1", 32, "0.0.0.0"), profile);

    private static object[] Case(string description, string json) => [description, json];
    private static JsonObject KeyNode(JsonObject json) => json["Key"]!.AsObject();
    private static JsonObject ProfileNode(JsonObject json) => json["Profile"]!.AsObject();
    private static string Ipv4(Action<JsonObject> mutate) => Mutate(Identity(Profile(100, 80)), mutate);
    private static string Ipv6(Action<JsonObject> mutate) => Mutate(new ExactRouteMutationIdentity(1,
        Key(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1", 7),
        new NativeRouteProfile(128, 100, 80, 5, 3, false, false, false, true)), mutate);
    private static string Mutate(ExactRouteMutationIdentity identity, Action<JsonObject> mutate)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(identity))!.AsObject();
        mutate(json);
        return json.ToJsonString();
    }
}
