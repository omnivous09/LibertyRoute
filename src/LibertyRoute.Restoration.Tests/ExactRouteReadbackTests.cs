using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using LibertyRoute.RouteObservation;

namespace LibertyRoute.Restoration.Tests;

public sealed class ExactRouteReadbackTests
{
    [Fact]
    public void NativeAbiLayoutMatchesSupportedWindowsLayout()
    {
        Assert.Equal(8, Marshal.SizeOf<ulong>());
        Assert.Equal(4, Marshal.SizeOf<uint>());
        Assert.Equal(1, Marshal.SizeOf<byte>());
        Assert.Contains(IntPtr.Size, new[] { 4, 8 });
        Assert.Equal(28, NativeRouteAbi.SockaddrSize);
        Assert.Equal(32, NativeRouteAbi.PrefixSize);
        Assert.Equal(104, NativeRouteAbi.RowSize);
        Assert.Equal(8, NativeRouteAbi.TableFirstRowOffset);
        Assert.Equal(0, Marshal.OffsetOf<ExactNativeSockaddrInet>(nameof(ExactNativeSockaddrInet.Family)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<ExactNativeSockaddrInet>(nameof(ExactNativeSockaddrInet.Ipv4Address)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<ExactNativeSockaddrInet>(nameof(ExactNativeSockaddrInet.Ipv6Address)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<ExactNativeSockaddrInet>(nameof(ExactNativeSockaddrInet.Ipv6ScopeId)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<ExactNativeIpAddressPrefix>(nameof(ExactNativeIpAddressPrefix.Prefix)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<ExactNativeIpAddressPrefix>(nameof(ExactNativeIpAddressPrefix.PrefixLength)).ToInt32());
        Assert.Equal(0, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.InterfaceLuid)));
        Assert.Equal(8, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.InterfaceIndex)));
        Assert.Equal(12, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.DestinationPrefix)));
        Assert.Equal(44, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.NextHop)));
        Assert.Equal(72, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.SitePrefixLength)));
        Assert.Equal(76, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.ValidLifetime)));
        Assert.Equal(80, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.PreferredLifetime)));
        Assert.Equal(84, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Metric)));
        Assert.Equal(88, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Protocol)));
        Assert.Equal(92, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Loopback)));
        Assert.Equal(93, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.AutoconfigureAddress)));
        Assert.Equal(94, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Publish)));
        Assert.Equal(95, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Immortal)));
        Assert.Equal(96, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Age)));
        Assert.Equal(100, NativeRouteAbi.OffsetOfRowField(nameof(ExactNativeMibIpForwardRow2.Origin)));
    }

    [Fact]
    public void RawIpv4RowFixtureUsesIndependentWindowsAbiOffsets()
    {
        var raw = RawRow(NativeRouteAddressFamily.IPv4, "192.0.2.99", 32, "0.0.0.0", 0);
        var decoded = DecodeRawRow(raw, NativeRouteAddressFamily.IPv4);
        Assert.Equal("192.0.2.99", decoded.Key.DestinationAddress);
        Assert.Equal("0.0.0.0", decoded.Key.NextHopAddress);
        Assert.Equal(42UL, decoded.Key.InterfaceLuid);
        Assert.Equal(7U, decoded.InterfaceIndex);
        Assert.Equal(uint.MaxValue, decoded.PreferredLifetime);
        Assert.Equal(5U, decoded.Metric);
    }

    [Fact]
    public void RawIpv6RowFixtureUsesIndependentWindowsAbiOffsetsAndScope()
    {
        var raw = RawRow(NativeRouteAddressFamily.IPv6, "2001:db8::1", 128, "fe80::1", 0xfedcba98);
        var decoded = DecodeRawRow(raw, NativeRouteAddressFamily.IPv6);
        Assert.Equal("2001:db8::1", decoded.Key.DestinationAddress);
        Assert.Equal("fe80::1", decoded.Key.NextHopAddress);
        Assert.Equal(0xfedcba98, decoded.Key.NextHopScopeId);
    }

    [Fact]
    public void Ipv4FixtureDecodesCanonically()
    {
        var decoded = NativeRouteDecoder.Decode(NativeRow("192.0.2.99", 32, "0.0.0.0"), NativeRouteAddressFamily.IPv4);
        Assert.Equal("192.0.2.99", decoded.Key.DestinationAddress);
        Assert.Equal("0.0.0.0", decoded.Key.NextHopAddress);
        Assert.Equal(0, decoded.Key.NextHopScopeId);
    }

    [Fact]
    public void Ipv6FixturePreservesNextHopScopeAndCanonicalizes()
    {
        var decoded = NativeRouteDecoder.Decode(NativeRow("2001:0db8:0:0:0:0:0:1", 128, "fe80::1", scope: 17), NativeRouteAddressFamily.IPv6);
        Assert.Equal("2001:db8::1", decoded.Key.DestinationAddress);
        Assert.Equal("fe80::1", decoded.Key.NextHopAddress);
        Assert.Equal(17, decoded.Key.NextHopScopeId);
    }

    [Fact]
    public void UnsupportedOrMismatchedFamilyIsRejected()
    {
        var row = NativeRow("192.0.2.1", 32, "0.0.0.0");
        row.NextHop.Family = 99;
        Assert.Throws<InvalidOperationException>(() => NativeRouteDecoder.Decode(row, NativeRouteAddressFamily.IPv4));
        Assert.Throws<ArgumentOutOfRangeException>(() => NativeRouteKey.Create((NativeRouteAddressFamily)99,
            IPAddress.Parse("2001:db8::1"), 32, IPAddress.IPv6Any, 1));
    }

    [Theory]
    [InlineData("192.0.2.1", 33)]
    [InlineData("2001:db8::1", 129)]
    public void InvalidPrefixIsRejected(string address, byte prefix)
    {
        var row = NativeRow(address, prefix, address.Contains(':') ? "::" : "0.0.0.0");
        var family = address.Contains(':') ? NativeRouteAddressFamily.IPv6 : NativeRouteAddressFamily.IPv4;
        Assert.Throws<InvalidOperationException>(() => NativeRouteDecoder.Decode(row, family));
    }

    [Fact]
    public void MalformedSocketAndBooleanEvidenceIsRejected()
    {
        var ipv6 = NativeRow("2001:db8::1", 64, "::");
        ipv6.NextHop.Ipv6Address = [1, 2];
        Assert.Throws<InvalidOperationException>(() => NativeRouteDecoder.Decode(ipv6, NativeRouteAddressFamily.IPv6));

        var ipv4 = NativeRow("192.0.2.1", 32, "0.0.0.0");
        ipv4.Publish = 2;
        Assert.Throws<InvalidOperationException>(() => NativeRouteDecoder.Decode(ipv4, NativeRouteAddressFamily.IPv4));
    }

    [Fact]
    public void ExactlyOneFullAcceptableMatchIsVerified()
    {
        var row = Row();
        var result = ExactRouteVerifier.VerifyPresent(Complete(row), row.Key, Profile(row));
        Assert.Equal(ExactRouteVerificationStatus.VerifiedPresent, result.Status);
        Assert.Equal(1, result.FullKeyMatchCount);
        Assert.Equal(1, result.ReducedIdentityMatchCount);
    }

    [Fact]
    public void NoMatchIsDistinguished()
    {
        var expected = Row();
        var other = Row("198.51.100.1");
        Assert.Equal(ExactRouteVerificationStatus.NoMatch,
            ExactRouteVerifier.VerifyPresent(Complete(other), expected.Key, Profile(expected)).Status);
    }

    [Fact]
    public void DuplicateFullMatchesAreRejected()
    {
        var row = Row();
        Assert.Equal(ExactRouteVerificationStatus.DuplicateFullKeyMatches,
            ExactRouteVerifier.VerifyPresent(Complete(row, row), row.Key, Profile(row)).Status);
    }

    [Fact]
    public void ReducedIdentityCollisionIsRejected()
    {
        var expected = Row();
        var collision = Row() with { Key = Row().Key with { InterfaceLuid = 999 } };
        Assert.Equal(ExactRouteVerificationStatus.ReducedIdentityCollision,
            ExactRouteVerifier.VerifyPresent(Complete(expected, collision), expected.Key, Profile(expected)).Status);
    }

    [Theory]
    [InlineData("metric")]
    [InlineData("index")]
    [InlineData("protocol")]
    [InlineData("sitePrefix")]
    [InlineData("validLifetime")]
    [InlineData("preferredLifetime")]
    public void ExpectedProfileChangesAreRejected(string field)
    {
        var expected = Row();
        var actual = field switch
        {
            "metric" => expected with { Metric = expected.Metric + 1 },
            "index" => expected with { InterfaceIndex = expected.InterfaceIndex + 1 },
            "protocol" => expected with { Protocol = expected.Protocol + 1 },
            "sitePrefix" => expected with { SitePrefixLength = (byte)(expected.SitePrefixLength - 1) },
            "validLifetime" => expected with { ValidLifetime = 101, PreferredLifetime = 100 },
            _ => expected with { ValidLifetime = 102, PreferredLifetime = 101 }
        };
        Assert.Equal(ExactRouteVerificationStatus.ExpectedProfileMismatch,
            ExactRouteVerifier.VerifyPresent(Complete(actual), expected.Key, Profile(expected) with
            {
                ValidLifetime = new(100), PreferredLifetime = new(100)
            }).Status);
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("autoconfigure")]
    [InlineData("publish")]
    [InlineData("immortal")]
    public void EveryBooleanExpectedProfileFieldIsChecked(string field)
    {
        var expected = Row();
        var actual = field switch
        {
            "loopback" => expected with { Loopback = !expected.Loopback },
            "autoconfigure" => expected with { AutoconfigureAddress = !expected.AutoconfigureAddress },
            "publish" => expected with { Publish = !expected.Publish },
            _ => expected with { Immortal = !expected.Immortal }
        };
        Assert.Equal(ExactRouteVerificationStatus.ExpectedProfileMismatch,
            ExactRouteVerifier.VerifyPresent(Complete(actual), expected.Key, Profile(expected)).Status);
    }

    [Fact]
    public void OriginAndAgeAreObservationalNotCallerControlledEquality()
    {
        var expected = Row();
        var actual = expected with { Origin = 999, Age = 123456 };
        Assert.Equal(ExactRouteVerificationStatus.VerifiedPresent,
            ExactRouteVerifier.VerifyPresent(Complete(actual), expected.Key, Profile(expected)).Status);
    }

    [Fact]
    public void FiniteLifetimesMayCountDownButNotIncrease()
    {
        var expected = Row() with { ValidLifetime = 100, PreferredLifetime = 80 };
        var actual = expected with { ValidLifetime = 90, PreferredLifetime = 70 };
        Assert.Equal(ExactRouteVerificationStatus.VerifiedPresent,
            ExactRouteVerifier.VerifyPresent(Complete(actual), expected.Key, Profile(expected)).Status);
        Assert.Equal(ExactRouteVerificationStatus.ExpectedProfileMismatch,
            ExactRouteVerifier.VerifyPresent(Complete(actual with { ValidLifetime = 101 }), expected.Key, Profile(expected)).Status);
    }

    [Theory]
    [InlineData("luid")]
    [InlineData("nextHop")]
    [InlineData("scope")]
    [InlineData("destination")]
    [InlineData("prefix")]
    public void NativeKeyChangesNeverVerify(string field)
    {
        var expected = Row();
        var changedKey = field switch
        {
            "luid" => expected.Key with { InterfaceLuid = 99 },
            "nextHop" => expected.Key with { NextHopAddress = "192.0.2.254" },
            "scope" => KeyV6() with { NextHopScopeId = 8 },
            "destination" => expected.Key with { DestinationAddress = "192.0.2.2" },
            _ => expected.Key with { DestinationPrefixLength = 31 }
        };
        var actual = field == "scope" ? RowV6() with { Key = changedKey } : expected with { Key = changedKey };
        var expectedRow = field == "scope" ? RowV6() : expected;
        Assert.NotEqual(ExactRouteVerificationStatus.VerifiedPresent,
            ExactRouteVerifier.VerifyPresent(Complete(actual), expectedRow.Key, Profile(expectedRow)).Status);
    }

    [Fact]
    public void IncompleteNativeReadAndOversizedResultCannotVerify()
    {
        var expected = Row();
        var readFailure = new ExactRouteObservation([], false, true, false, ["IPv4 native read failed."]);
        var oversized = new ExactRouteObservation(Enumerable.Repeat(expected, WindowsExactRouteReader.MaximumRoutes).ToArray(),
            true, true, true, ["Native row count exceeds 4096."]);
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(readFailure, expected.Key, Profile(expected)).Status);
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(oversized, expected.Key, Profile(expected)).Status);
        var count = WindowsExactRouteReader.AssessNativeCount(4097, WindowsExactRouteReader.MaximumRoutes);
        Assert.False(count.CanMaterialize);
        Assert.True(count.Truncated);
        Assert.Equal(0, count.RowsToRead);
    }

    [Fact]
    public void MalformedSemanticEvidenceCannotBypassDecoderValidation()
    {
        var expected = Row();
        var malformedFamily = expected with { Key = expected.Key with { AddressFamily = (NativeRouteAddressFamily)99 } };
        var malformedPrefix = expected with { Key = expected.Key with { DestinationPrefixLength = 33 } };
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(malformedFamily), expected.Key, Profile(expected)).Status);
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(malformedPrefix), expected.Key, Profile(expected)).Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void MalformedIpv6ExpectedAndObservedScopesCannotVerify(long scope)
    {
        var expected = RowV6();
        var malformedKey = expected.Key with { NextHopScopeId = scope };
        var malformedObserved = expected with { Key = malformedKey };
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(expected), malformedKey, Profile(expected)).Status);
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(malformedObserved), expected.Key, Profile(expected)).Status);
    }

    [Fact]
    public void Ipv6MaximumScopeIsValidAndNextValueIsRejected()
    {
        var expected = RowV6();
        var maximum = expected with { Key = expected.Key with { NextHopScopeId = uint.MaxValue } };
        Assert.Equal(ExactRouteVerificationStatus.VerifiedPresent,
            ExactRouteVerifier.VerifyPresent(Complete(maximum), maximum.Key, Profile(maximum)).Status);
        var tooLarge = maximum.Key with { NextHopScopeId = (long)uint.MaxValue + 1 };
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(maximum), tooLarge, Profile(maximum)).Status);
    }

    [Fact]
    public void Ipv4NonzeroScopeCannotVerify()
    {
        var expected = Row();
        var malformed = expected.Key with { NextHopScopeId = 1 };
        Assert.Equal(ExactRouteVerificationStatus.IncompleteObservation,
            ExactRouteVerifier.VerifyPresent(Complete(expected), malformed, Profile(expected)).Status);
    }

    [Theory]
    [InlineData(50U, 100U, false)]
    [InlineData(0U, 1U, false)]
    [InlineData(50U, uint.MaxValue, false)]
    [InlineData(uint.MaxValue, 50U, true)]
    [InlineData(uint.MaxValue, uint.MaxValue, true)]
    [InlineData(0U, 0U, true)]
    public void ExpectedLifetimeProfileRelationshipIsValidated(uint valid, uint preferred, bool accepted)
    {
        var expected = Row() with { ValidLifetime = valid, PreferredLifetime = preferred };
        var profile = Profile(expected);
        var actual = expected;
        var status = ExactRouteVerifier.VerifyPresent(Complete(actual), expected.Key, profile).Status;
        Assert.Equal(accepted ? ExactRouteVerificationStatus.VerifiedPresent : ExactRouteVerificationStatus.IncompleteObservation, status);
    }

    [Fact]
    public void AbsenceRequiresZeroFullAndReducedIdentityRows()
    {
        var expected = Row();
        Assert.Equal(ExactRouteVerificationStatus.VerifiedAbsent,
            ExactRouteVerifier.VerifyAbsent(Complete(), expected.Key).Status);
        Assert.NotEqual(ExactRouteVerificationStatus.VerifiedAbsent,
            ExactRouteVerifier.VerifyAbsent(Complete(expected), expected.Key).Status);
        var collision = expected with { Key = expected.Key with { InterfaceLuid = 999 } };
        Assert.Equal(ExactRouteVerificationStatus.ReducedIdentityCollision,
            ExactRouteVerifier.VerifyAbsent(Complete(collision), expected.Key).Status);
    }

    [Fact]
    public void VerificationDataCannotBeSuppliedToAnOperationDecisionApi()
    {
        var assembly = typeof(ExactRouteVerifier).Assembly;
        Assert.Null(assembly.GetType("LibertyRoute.RouteObservation.ReadBackDecisionPolicy"));
        Assert.Null(assembly.GetType("LibertyRoute.RouteObservation.ReadBackDecision"));

        var authorityInputs = new[]
        {
            typeof(ExactRouteVerifier.Verification),
            typeof(ExactRouteVerificationStatus),
            typeof(bool)
        };
        var dangerousMethods = assembly.GetExportedTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            .Where(method => method.GetParameters().Any(parameter => authorityInputs.Contains(parameter.ParameterType)))
            .Where(method => method.Name.Contains("Operation", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Decision", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Applied", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Reverted", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Empty(dangerousMethods);
    }

    [Fact]
    public void ObservationAssemblyHasOnlyApprovedNativeImportsAndNoMutationAuthority()
    {
        var assembly = typeof(ExactRouteVerifier).Assembly;
        var imports = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            .SelectMany(method => method.GetCustomAttributes(typeof(DllImportAttribute), false)
                .Cast<DllImportAttribute>().Select(import => $"{import.Value}!{method.Name}"))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["iphlpapi.dll!FreeMibTable", "iphlpapi.dll!GetIpForwardTable2"], imports);

        var projectDirectory = FindRepositoryDirectory("tools", "LibertyRoute.RouteObservation");
        var source = string.Join('\n', Directory.EnumerateFiles(projectDirectory, "*.cs").Select(File.ReadAllText));
        foreach (var forbidden in ForbiddenMutationSymbols)
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] ForbiddenMutationSymbols =
    [
        "CreateIpForwardEntry2", "DeleteIpForwardEntry2", "SetIpForwardEntry", "InitializeIpForwardEntry",
        "CreateUnicastIpAddressEntry", "DeleteUnicastIpAddressEntry", "New-NetRoute", "Remove-NetRoute",
        "Set-NetRoute", "netsh", "Process.Start", "PowerShell", "RouteRestorationProvider",
        "WindowsRouteMutationNative", "IRestorationMutationProvider", "RecordedMutationExecutor",
        "MutationOwnershipCoordinator", "RecoveryExecutionCoordinator"
    ];

    private static ExactRouteObservation Complete(params ExactNativeRouteRow[] rows) => new(rows, true, true, false, []);

    private static ExactNativeRouteRow Row(string destination = "192.0.2.1") => new(
        NativeRouteKey.Create(NativeRouteAddressFamily.IPv4, IPAddress.Parse(destination), 32, IPAddress.Any, 42),
        7, 32, uint.MaxValue, uint.MaxValue, 5, 3, false, false, false, true, 1, 2);

    private static NativeRouteKey KeyV6() => NativeRouteKey.Create(NativeRouteAddressFamily.IPv6,
        IPAddress.Parse("2001:db8::1"), 128, new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 7), 42);

    private static ExactNativeRouteRow RowV6() => Row() with { Key = KeyV6(), SitePrefixLength = 128 };

    private static NativeRouteExpectedProfile Profile(ExactNativeRouteRow row) => new(row.InterfaceIndex,
        row.SitePrefixLength, new(row.ValidLifetime), new(row.PreferredLifetime), row.Metric, row.Protocol,
        row.Loopback, row.AutoconfigureAddress, row.Publish, row.Immortal);

    private static ExactNativeMibIpForwardRow2 NativeRow(
        string destination, byte prefix, string nextHop, uint scope = 0)
    {
        var destinationAddress = IPAddress.Parse(destination);
        var nextHopAddress = IPAddress.Parse(nextHop);
        return new ExactNativeMibIpForwardRow2
        {
            InterfaceLuid = 42,
            InterfaceIndex = 7,
            DestinationPrefix = new ExactNativeIpAddressPrefix
            {
                Prefix = Socket(destinationAddress),
                PrefixLength = prefix
            },
            NextHop = Socket(nextHopAddress, scope),
            SitePrefixLength = prefix > (destinationAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
                ? (byte)0
                : prefix,
            ValidLifetime = uint.MaxValue,
            PreferredLifetime = uint.MaxValue,
            Metric = 5,
            Protocol = 3,
            Immortal = 1,
            Origin = 2
        };
    }

    private static ExactNativeSockaddrInet Socket(IPAddress address, uint scope = 0)
    {
        var bytes = address.GetAddressBytes();
        return new ExactNativeSockaddrInet
        {
            Family = (ushort)(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? NativeRouteAddressFamily.IPv4 : NativeRouteAddressFamily.IPv6),
            Ipv4Address = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? BitConverter.ToUInt32(bytes) : 0,
            Ipv6Address = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? bytes : new byte[16],
            Ipv6ScopeId = scope
        };
    }

    private static ExactNativeRouteRow DecodeRawRow(byte[] raw, NativeRouteAddressFamily family)
    {
        var pointer = Marshal.AllocHGlobal(raw.Length);
        try
        {
            Marshal.Copy(raw, 0, pointer, raw.Length);
            return NativeRouteDecoder.DecodeRowPointer(pointer, family);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static byte[] RawRow(
        NativeRouteAddressFamily family, string destination, byte prefix, string nextHop, uint scope)
    {
        // These literals are independently specified Windows ABI offsets, not values obtained
        // from the managed structures under test.
        var raw = new byte[104];
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0, 8), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8, 4), 7);
        WriteSocket(raw, 12, family, IPAddress.Parse(destination), 0);
        raw[40] = prefix;
        WriteSocket(raw, 44, family, IPAddress.Parse(nextHop), scope);
        raw[72] = prefix;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(76, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(80, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(84, 4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(88, 4), 3);
        raw[95] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(96, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(100, 4), 2);
        return raw;
    }

    private static void WriteSocket(
        byte[] raw, int offset, NativeRouteAddressFamily family, IPAddress address, uint scope)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(offset, 2), (ushort)family);
        var bytes = address.GetAddressBytes();
        if (family == NativeRouteAddressFamily.IPv4)
            bytes.CopyTo(raw, offset + 4);
        else
        {
            bytes.CopyTo(raw, offset + 8);
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset + 24, 4), scope);
        }
    }

    private static string FindRepositoryDirectory(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
