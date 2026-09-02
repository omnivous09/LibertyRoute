using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Tests;

public sealed class ExactRouteOwnershipEvidenceBindingTests
{
    private static readonly Guid SessionId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ChangeId = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly Guid AttemptId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

    private static ExactRouteMutationIdentity Identity(bool ipv6 = false) => new(
        ExactRouteMutationIdentity.CurrentSchemaVersion,
        NativeRouteKey.Create(ipv6 ? NativeRouteAddressFamily.IPv6 : NativeRouteAddressFamily.IPv4,
            IPAddress.Parse(ipv6 ? "2001:db8:1::" : "192.0.2.0"), ipv6 ? (byte)64 : (byte)24,
            IPAddress.Parse(ipv6 ? "2001:db8::1" : "192.0.2.1"), 0x0102030405060708),
        new NativeRouteProfile(ipv6 ? (byte)64 : (byte)24, 3600, 1800, 9, 3,
            false, true, false, true));

    private static PersistedOwnedChange Record(OwnedChangeLifecycle lifecycle = OwnedChangeLifecycle.Applied,
        ExactRouteMutationIdentity? identity = null, int? sequence = 7) =>
        PersistedOwnedChange.CreateExactRoute(SessionId, ChangeId, DryRunOperationCategory.Route,
            "route-π", "before", "after", DateTimeOffset.Parse("2026-09-01T02:03:04.0000000Z"),
            sequence, OwnershipEvidenceSource.MutationLedger, lifecycle, identity ?? Identity(), AttemptId);

    private static PersistedOwnedChange Resign(PersistedOwnedChange record) => record with
    {
        ExactRouteEvidenceFingerprint = ExactRouteOwnershipEvidenceBinding.ComputeFingerprint(record)
    };

    private static void AssertSensitive(string field, PersistedOwnedChange baseline, PersistedOwnedChange candidate)
    {
        var variant = Resign(candidate);
        Assert.Empty(PersistedOwnedChange.Validate(variant));
        Assert.NotEqual(baseline.ExactRouteEvidenceFingerprint, variant.ExactRouteEvidenceFingerprint);
        Assert.NotEmpty(PersistedOwnedChange.Validate(variant with
        {
            ExactRouteEvidenceFingerprint = baseline.ExactRouteEvidenceFingerprint
        }));
    }

    private static ExactRouteMutationIdentity V4(
        string destination = "C0000200", byte prefix = 24, string nextHop = "C0000201",
        ulong luid = 0x0102030405060708, byte sitePrefix = 24, uint valid = 3600,
        uint preferred = 1800, uint metric = 9, bool loopback = false,
        bool autoconfigure = true, bool publish = false, bool immortal = true) => new(1,
            new NativeRouteKey(NativeRouteAddressFamily.IPv4, destination, prefix, nextHop, 0, luid),
            new NativeRouteProfile(sitePrefix, valid, preferred, metric, 3, loopback,
                autoconfigure, publish, immortal));

    private static ExactRouteMutationIdentity V6(uint scope = 1) => new(1,
        new NativeRouteKey(NativeRouteAddressFamily.IPv6,
            "20010DB8000100000000000000000000", 64,
            "FE800000000000000000000000000001", scope, 0x0102030405060708),
        new NativeRouteProfile(64, 3600, 1800, 9, 3, false, true, false, true));

    [Fact]
    public void CompleteCanonicalVectorAndDigestAreIndependentAndFrozen()
    {
        var bytes = ExactRouteOwnershipEvidenceBinding.GetCanonicalBytes(Record());
        // Segments, in order: domain+NUL; evidence version; three RFC-4122 GUIDs;
        // purpose/category/source; UTF-8 length+value for target/original/applied;
        // UTC ticks; sequence presence+value; Core schema; family; destination+prefix;
        // next hop+scope+LUID; site prefix; valid/preferred lifetime; metric; protocol;
        // loopback/autoconfigure/publish/immortal.
        const string ExpectedCanonicalHex =
            "4C696265727479526F7574652F4578616374526F7574654F776E65727368697045766964656E63652F763100" +
            "00000001" +
            "00112233445566778899AABBCCDDEEFF102132435465768798A9BACBDCEDFE0FFFEEDDCCBBAA99887766554433221100" +
            "000000000000000200000001" +
            "00000008726F7574652DCF80000000066265666F7265000000056166746572" +
            "08DF07CD288E3C00" +
            "0100000007" +
            "000000010002C000020018C0000201000000000102030405060708" +
            "1800000E10000007080000000900000003" +
            "00010001";
        const string ExpectedDigest = "430E009DED31F9E103F586FD94039E25AE887F3EE91C2C7A91EE43703FDFBDBC";
        var expectedBytes = Convert.FromHexString(ExpectedCanonicalHex);
        Assert.Equal(200, expectedBytes.Length);
        Assert.Equal(ExpectedDigest, Convert.ToHexString(SHA256.HashData(expectedBytes)));
        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(ExpectedDigest, Record().ExactRouteEvidenceFingerprint);

        // Fixed-only contract fields: v1 evidence version, SessionMutation purpose,
        // Core schema v1, and NETMGMT protocol 3. Unsupported semantic alternates
        // cannot form valid records, so byte mutation proves their digest positions.
        foreach (var offset in new[] { 44, 96, 152, 192 })
        {
            var changed = expectedBytes.ToArray();
            changed[offset + 3]++;
            Assert.NotEqual(ExpectedDigest, Convert.ToHexString(SHA256.HashData(changed)));
        }
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(expectedBytes.AsSpan(44, 4)));
        Assert.Equal((int)RecordPurpose.SessionMutation, BinaryPrimitives.ReadInt32BigEndian(expectedBytes.AsSpan(96, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(expectedBytes.AsSpan(152, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(expectedBytes.AsSpan(192, 4)));
    }

    [Fact]
    public void FingerprintMatchesKnownAnswerAndReplaysDeterministically()
    {
        var record = Record();
        Assert.Equal("430E009DED31F9E103F586FD94039E25AE887F3EE91C2C7A91EE43703FDFBDBC",
            record.ExactRouteEvidenceFingerprint);
        Assert.Equal(record.ExactRouteEvidenceFingerprint,
            ExactRouteOwnershipEvidenceBinding.ComputeFingerprint(record));
    }

    [Fact]
    public void EverySemanticallyVariableBoundFieldChangesFingerprintAndRejectsReplay()
    {
        var baseline = Record();
        var cases = new (string Field, PersistedOwnedChange Base, PersistedOwnedChange Candidate)[]
        {
            ("SessionId", baseline, baseline with { SessionId = Guid.Parse("10112233-4455-6677-8899-aabbccddeeff") }),
            ("ChangeId", baseline, baseline with { ChangeId = Guid.Parse("20213243-5465-7687-98a9-bacbdcedfe0f") }),
            ("MutationAttemptId", baseline, baseline with { MutationAttemptId = Guid.Parse("afeeddcc-bbaa-9988-7766-554433221100") }),
            ("Category", baseline, baseline with { Category = DryRunOperationCategory.Dns }),
            ("EvidenceSource", baseline, baseline with { EvidenceSource = OwnershipEvidenceSource.TransactionJournal }),
            ("TargetIdentity", baseline, baseline with { TargetIdentity = "other" }),
            ("OriginalValue", baseline, baseline with { OriginalValue = "other" }),
            ("AppliedValue", baseline, baseline with { AppliedValue = "other" }),
            ("RecordedAtUtc", baseline, baseline with { RecordedAtUtc = baseline.RecordedAtUtc.AddTicks(1) }),
            ("SequenceAbsent", baseline, baseline with { SequenceNumber = null }),
            ("SequenceValue", baseline, baseline with { SequenceNumber = 8 }),
            // Family changes address widths and therefore requires the smallest complete valid IPv6 identity.
            ("AddressFamily", baseline, baseline with { ExactRouteIdentity = Identity(ipv6: true) }),
            ("DestinationAddress", baseline, baseline with { ExactRouteIdentity = V4(destination: "C6336400") }),
            ("DestinationPrefixLength", baseline, baseline with { ExactRouteIdentity = V4(prefix: 25) }),
            ("NextHopAddress", baseline, baseline with { ExactRouteIdentity = V4(nextHop: "C0000202") }),
            ("InterfaceLuid", baseline, baseline with { ExactRouteIdentity = V4(luid: 0x1112131415161718) }),
            ("SitePrefixLength", baseline, baseline with { ExactRouteIdentity = V4(sitePrefix: 23) }),
            ("ValidLifetime", baseline, baseline with { ExactRouteIdentity = V4(valid: 3601) }),
            ("PreferredLifetime", baseline, baseline with { ExactRouteIdentity = V4(preferred: 1799) }),
            ("Metric", baseline, baseline with { ExactRouteIdentity = V4(metric: 10) }),
            ("Loopback", baseline, baseline with { ExactRouteIdentity = V4(loopback: true) }),
            ("AutoconfigureAddress", baseline, baseline with { ExactRouteIdentity = V4(autoconfigure: false) }),
            ("Publish", baseline, baseline with { ExactRouteIdentity = V4(publish: true) }),
            ("Immortal", baseline, baseline with { ExactRouteIdentity = V4(immortal: false) }),
            ("NextHopScopeId", Record(identity: V6(1)), Record(identity: V6(2)))
        };
        Assert.All(cases, item => AssertSensitive(item.Field, item.Base, item.Candidate));

        var planned = baseline with { Lifecycle = OwnedChangeLifecycle.Planned, IsComplete = false };
        Assert.Equal(baseline.ExactRouteEvidenceFingerprint,
            ExactRouteOwnershipEvidenceBinding.ComputeFingerprint(planned));
    }

    [Theory]
    [InlineData(OwnedChangeLifecycle.Planned, false)]
    [InlineData(OwnedChangeLifecycle.Applied, true)]
    [InlineData(OwnedChangeLifecycle.Reverted, false)]
    public void StructuralPredicateRequiresValidCompleteAppliedEvidence(OwnedChangeLifecycle lifecycle, bool expected)
    {
        Assert.Equal(expected, Record(lifecycle).HasValidAppliedExactRouteOwnershipEvidence);
    }

    [Fact]
    public void FingerprintTamperingAndNonUtcTimestampFailClosed()
    {
        var record = Record();
        Assert.NotEmpty(PersistedOwnedChange.Validate(record with
        {
            ExactRouteEvidenceFingerprint = new string('0', 64)
        }));
        Assert.Throws<InvalidOperationException>(() => PersistedOwnedChange.CreateExactRoute(
            SessionId, ChangeId, DryRunOperationCategory.Route, "route", "before", "after",
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(8)), 1,
            OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned, Identity(), AttemptId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void EveryPartialExactEvidenceShapeFailsClosed(int shape)
    {
        var legacy = PersistedOwnedChange.Create(SessionId, ChangeId, DryRunOperationCategory.Route,
            "route", "before", "after", DateTimeOffset.Parse("2026-09-01T00:00:00Z"), 1,
            OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Planned);
        var partial = shape switch
        {
            1 => legacy with { ExactRouteEvidenceVersion = 1 },
            2 => legacy with { ExactRouteIdentity = Identity() },
            3 => legacy with { MutationAttemptId = AttemptId },
            _ => legacy with { ExactRouteEvidenceFingerprint = new string('A', 64) }
        };
        Assert.NotEmpty(PersistedOwnedChange.Validate(partial));
        Assert.False(partial.HasValidAppliedExactRouteOwnershipEvidence);
    }

    [Fact]
    public void UnknownVersionAndRecoveryMutationExactEvidenceAreRejected()
    {
        var exact = Record();
        Assert.NotEmpty(PersistedOwnedChange.Validate(exact with { ExactRouteEvidenceVersion = 2 }));
        Assert.NotEmpty(PersistedOwnedChange.Validate(exact with
        {
            Purpose = RecordPurpose.RecoveryMutation,
            RecoveryAttemptId = Guid.NewGuid(), AuthorizationEvidenceId = Guid.NewGuid()
        }));
    }
}
