using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using LibertyRoute.Core;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class WindowsRouteMutationNativeTests
{
    [Theory]
    [InlineData(0u, RouteNativeStatus.Success)]
    [InlineData(5u, RouteNativeStatus.AccessDenied)]
    [InlineData(5010u, RouteNativeStatus.AlreadyExists)]
    [InlineData(1168u, RouteNativeStatus.NotFound)]
    [InlineData(87u, RouteNativeStatus.InvalidParameter)]
    [InlineData(12345u, RouteNativeStatus.Failed)]
    public void NativeStatusMappingIsDeterministic(uint status, RouteNativeStatus expected)
    {
        Assert.Equal(expected, WindowsRouteNativeStatusMapper.Map(status));
    }

    [Fact]
    public void NativeAbiSizesAndOffsetsMatchX64IpHelperLayout()
    {
        Assert.Equal(28, Marshal.SizeOf<NativeSockaddrInet>());
        Assert.Equal(32, Marshal.SizeOf<NativeIpAddressPrefix>());
        Assert.Equal(104, Marshal.SizeOf<NativeMibIpForwardRow2>());
        Assert.Equal(8, Marshal.SizeOf<NativeMibIpForwardTable2>());

        Assert.Equal(0, Marshal.OffsetOf<NativeSockaddrInet>(nameof(NativeSockaddrInet.Family)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NativeSockaddrInet>(nameof(NativeSockaddrInet.IPv4Address)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeSockaddrInet>(nameof(NativeSockaddrInet.IPv6Address)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.InterfaceLuid)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.InterfaceIndex)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.DestinationPrefix)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.NextHop)).ToInt32());
        Assert.Equal(84, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.Metric)).ToInt32());
        Assert.Equal(92, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.Loopback)).ToInt32());
        Assert.Equal(96, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.Age)).ToInt32());
        Assert.Equal(100, Marshal.OffsetOf<NativeMibIpForwardRow2>(nameof(NativeMibIpForwardRow2.Origin)).ToInt32());
    }

    [Theory]
    [InlineData("0.0.0.0", (byte)0, "0.0.0.0", RouteAddressFamily.IPv4)]
    [InlineData("192.0.2.0", (byte)24, "192.0.2.1", RouteAddressFamily.IPv4)]
    [InlineData("::", (byte)0, "::", RouteAddressFamily.IPv6)]
    [InlineData("2001:db8::1", (byte)128, "2001:db8::2", RouteAddressFamily.IPv6)]
    [InlineData("fe80::", (byte)64, "fe80::1", RouteAddressFamily.IPv6)]
    [InlineData("ff02::", (byte)16, "::", RouteAddressFamily.IPv6)]
    public void TranslationSetsAllRouteFields(string destination, byte prefixLength, string nextHop, RouteAddressFamily family)
    {
        var command = new RouteRestorationCommand(family, destination, prefixLength, nextHop, 17, 42, RouteMutationAction.Add);
        var row = WindowsRouteNativeTranslation.ToNativeRow(command);

        Assert.Equal((uint)0, row.InterfaceLuid);
        Assert.Equal((uint)17, row.InterfaceIndex);
        Assert.Equal(destination, row.DestinationPrefix.Address.ToString());
        Assert.Equal(prefixLength, row.DestinationPrefix.PrefixLength);
        Assert.Equal(nextHop, row.NextHop.ToIPAddress().ToString());
        Assert.Equal(prefixLength, row.SitePrefixLength);
        Assert.Equal(uint.MaxValue, row.ValidLifetime);
        Assert.Equal(uint.MaxValue, row.PreferredLifetime);
        Assert.Equal((uint)42, row.Metric);
        Assert.Equal((uint)3, row.Protocol);
        Assert.False(row.Loopback);
        Assert.False(row.AutoconfigureAddress);
        Assert.False(row.Publish);
        Assert.False(row.Immortal);
        Assert.Equal((uint)0, row.Age);
        Assert.Equal((uint)2, row.Origin);
    }

    [Theory]
    [InlineData(RouteAddressFamily.IPv4, "10.0.0.0", "2001:db8::1", (byte)24)]
    [InlineData(RouteAddressFamily.IPv4, "10.0.0.0", "10.0.0.1", (byte)33)]
    [InlineData(RouteAddressFamily.IPv6, "2001:db8::", "2001:db8::1", (byte)129)]
    public void TranslationRejectsInvalidFamilyOrPrefix(RouteAddressFamily family, string destination, string nextHop, byte prefixLength)
    {
        var command = new RouteRestorationCommand(family, destination, prefixLength, nextHop, 4, 1, RouteMutationAction.Add);

        Assert.ThrowsAny<ArgumentException>(() => WindowsRouteNativeTranslation.ToNativeRow(command));
    }

    [Fact]
    public async Task FakeEntryPointsReceiveTranslatedAddAndDeleteRows()
    {
        var api = new FakeWindowsRouteNativeApi();
        var native = new WindowsRouteMutationNative(api);
        var addCommand = Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add);
        var deleteCommand = addCommand with { Action = RouteMutationAction.Delete };

        Assert.True(await native.AddRouteAsync(addCommand, CancellationToken.None));
        Assert.True(await native.DeleteRouteAsync(deleteCommand, CancellationToken.None));

        Assert.Equal(2, api.InitializeCalls);
        Assert.Single(api.CreatedRows);
        Assert.Single(api.DeletedRows);
        Assert.Equal((uint)4, api.CreatedRows[0].InterfaceIndex);
        Assert.Equal((byte)24, api.CreatedRows[0].DestinationPrefix.PrefixLength);
    }

    [Fact]
    public async Task MutationPopulatesTheInitializedRowWithoutDiscardingInitialization()
    {
        var api = new FakeWindowsRouteNativeApi();
        var native = new WindowsRouteMutationNative(api);

        Assert.True(await native.AddRouteAsync(Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add), CancellationToken.None));

        var initializedRow = Assert.Single(api.CreatedRows);
        Assert.Equal(0x1122334455667788UL, initializedRow.InterfaceLuid);
        Assert.Equal((uint)4, initializedRow.InterfaceIndex);
        Assert.Equal((byte)24, initializedRow.DestinationPrefix.PrefixLength);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(5u, false)]
    [InlineData(5010u, false)]
    [InlineData(87u, false)]
    [InlineData(12345u, false)]
    public async Task FakeMutationStatusMapsToSuccessOrFailure(uint status, bool expectedSuccess)
    {
        var api = new FakeWindowsRouteNativeApi { CreateStatus = status };
        var native = new WindowsRouteMutationNative(api);

        var result = await native.AddRouteAsync(Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add), CancellationToken.None);

        Assert.Equal(expectedSuccess, result);
    }

    [Fact]
    public async Task CancellationPreventsNativeInvocation()
    {
        var api = new FakeWindowsRouteNativeApi();
        var native = new WindowsRouteMutationNative(api);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => native.AddRouteAsync(Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add), source.Token));

        Assert.Equal(0, api.InitializeCalls);
        Assert.Empty(api.CreatedRows);
        Assert.Empty(api.DeletedRows);
    }

    [Fact]
    public async Task QueryMapsMissingRouteWithoutMutation()
    {
        var api = new FakeWindowsRouteNativeApi();
        var native = new WindowsRouteMutationNative(api);

        var result = await native.QueryAsync(Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add), CancellationToken.None);

        Assert.False(result.Exists);
        Assert.Null(result.Route);
        Assert.Equal(RouteNativeStatus.Success, result.Status);
        Assert.Equal(1, api.QueryCalls);
    }

    [Fact]
    public async Task QueryMapsExactAndConflictingRoutes()
    {
        var command = Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add);
        var api = new FakeWindowsRouteNativeApi
        {
            TableRows = new[]
            {
                WindowsRouteNativeTranslation.ToNativeRow(command with { NextHop = "192.0.2.9" }),
                WindowsRouteNativeTranslation.ToNativeRow(command)
            }
        };
        var native = new WindowsRouteMutationNative(api);

        var result = await native.QueryAsync(command, CancellationToken.None);

        Assert.True(result.Exists);
        Assert.NotNull(result.Route);
        Assert.Equal("192.0.2.1", result.Route!.NextHop);
        Assert.Equal(RouteNativeStatus.Success, result.Status);
    }

    [Fact]
    public async Task QueryReturnsFirstConflictWhenNoExactMatchExists()
    {
        var command = Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add);
        var api = new FakeWindowsRouteNativeApi
        {
            TableRows = new[]
            {
                WindowsRouteNativeTranslation.ToNativeRow(command with { NextHop = "192.0.2.9" }),
                WindowsRouteNativeTranslation.ToNativeRow(command with { NextHop = "192.0.2.10" })
            }
        };

        var result = await new WindowsRouteMutationNative(api).QueryAsync(command, CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Equal("192.0.2.9", result.Route!.NextHop);
    }

    [Fact]
    public async Task QueryReturnsExactMatchWhenExactRowIsFirst()
    {
        var command = Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add);
        var api = new FakeWindowsRouteNativeApi
        {
            TableRows = new[]
            {
                WindowsRouteNativeTranslation.ToNativeRow(command),
                WindowsRouteNativeTranslation.ToNativeRow(command with { NextHop = "192.0.2.9" })
            }
        };

        var result = await new WindowsRouteMutationNative(api).QueryAsync(command, CancellationToken.None);

        Assert.True(result.Exists);
        Assert.Equal("192.0.2.1", result.Route!.NextHop);
    }

    [Fact]
    public async Task QueryReturnsMissingWhenNoMatchingPrefixExists()
    {
        var command = Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add);
        var api = new FakeWindowsRouteNativeApi
        {
            TableRows = new[]
            {
                WindowsRouteNativeTranslation.ToNativeRow(command with { DestinationAddress = "198.51.100.0" })
            }
        };

        var result = await new WindowsRouteMutationNative(api).QueryAsync(command, CancellationToken.None);

        Assert.False(result.Exists);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task QueryStatusFailureIsReturnedWithoutMutation()
    {
        var api = new FakeWindowsRouteNativeApi { QueryStatus = 5 };
        var native = new WindowsRouteMutationNative(api);

        var result = await native.QueryAsync(Command(RouteAddressFamily.IPv4, "192.0.2.0", 24, "192.0.2.1", RouteMutationAction.Add), CancellationToken.None);

        Assert.Equal(RouteNativeStatus.AccessDenied, result.Status);
        Assert.False(result.Exists);
    }

    [Fact]
    public void PInvokeDeclarationsAreIsolatedToWindowsProject()
    {
        var declarations = typeof(NativeMethods).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
            .Select(method => method.Name)
            .ToArray();

        Assert.Contains("CreateIpForwardEntry2", declarations);
        Assert.Contains("DeleteIpForwardEntry2", declarations);
        Assert.Contains("GetIpForwardTable2", declarations);
        Assert.DoesNotContain(typeof(WindowsRouteMutationNativeTests).Assembly.GetTypes(), type => type.Namespace == "LibertyRoute.Service");
    }

    private static RouteRestorationCommand Command(RouteAddressFamily family, string destination, byte prefixLength, string nextHop, RouteMutationAction action)
        => new(family, destination, prefixLength, nextHop, 4, 1, action);

    private sealed class FakeWindowsRouteNativeApi : IWindowsRouteNativeApi
    {
        public uint CreateStatus { get; set; }
        public uint DeleteStatus { get; set; }
        public uint QueryStatus { get; set; }
        public int InitializeCalls { get; private set; }
        public int QueryCalls { get; private set; }
        public List<NativeMibIpForwardRow2> CreatedRows { get; } = new();
        public List<NativeMibIpForwardRow2> DeletedRows { get; } = new();
        public IReadOnlyList<NativeMibIpForwardRow2> TableRows { get; set; } = Array.Empty<NativeMibIpForwardRow2>();

        public void InitializeIpForwardEntry(ref NativeMibIpForwardRow2 row)
        {
            InitializeCalls++;
            row.InterfaceLuid = 0x1122334455667788UL;
            row.SitePrefixLength = 0;
            row.ValidLifetime = uint.MaxValue;
            row.PreferredLifetime = uint.MaxValue;
        }

        public uint CreateIpForwardEntry2(ref NativeMibIpForwardRow2 row)
        {
            CreatedRows.Add(row);
            return CreateStatus;
        }

        public uint DeleteIpForwardEntry2(ref NativeMibIpForwardRow2 row)
        {
            DeletedRows.Add(row);
            return DeleteStatus;
        }

        public uint GetIpForwardTable2(ushort addressFamily, out IntPtr table)
        {
            QueryCalls++;
            table = IntPtr.Zero;
            if (QueryStatus != 0)
                return QueryStatus;

            var headerSize = Marshal.SizeOf<NativeMibIpForwardTable2>();
            var rowSize = Marshal.SizeOf<NativeMibIpForwardRow2>();
            table = Marshal.AllocHGlobal(headerSize + rowSize * TableRows.Count);
            Marshal.StructureToPtr(new NativeMibIpForwardTable2 { NumEntries = (uint)TableRows.Count }, table, false);
            for (var index = 0; index < TableRows.Count; index++)
                Marshal.StructureToPtr(TableRows[index], table + headerSize + rowSize * index, false);

            return 0;
        }

        public void FreeMibTable(IntPtr table)
        {
            if (table != IntPtr.Zero)
                Marshal.FreeHGlobal(table);
        }
    }
}
