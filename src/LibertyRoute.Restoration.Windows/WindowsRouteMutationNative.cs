using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Windows;

internal static class WindowsRouteNativeStatusMapper
{
    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorInvalidParameter = 87;
    public const uint ErrorNotFound = 1168;
    public const uint ErrorObjectAlreadyExists = 5010;

    public static RouteNativeStatus Map(uint status) => status switch
    {
        ErrorSuccess => RouteNativeStatus.Success,
        ErrorAccessDenied => RouteNativeStatus.AccessDenied,
        ErrorObjectAlreadyExists => RouteNativeStatus.AlreadyExists,
        ErrorNotFound => RouteNativeStatus.NotFound,
        ErrorInvalidParameter => RouteNativeStatus.InvalidParameter,
        _ => RouteNativeStatus.Failed
    };
}

[StructLayout(LayoutKind.Explicit, Size = 28, Pack = 4)]
internal struct NativeSockaddrInet
{
    [FieldOffset(0)]
    public ushort Family;

    [FieldOffset(4)]
    public uint IPv4Address;

    [FieldOffset(8)]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
    public byte[]? IPv6Address;

    public static NativeSockaddrInet FromAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return new NativeSockaddrInet
            {
                Family = (ushort)RouteAddressFamily.IPv4,
                IPv4Address = BitConverter.ToUInt32(bytes, 0),
                IPv6Address = new byte[16]
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return new NativeSockaddrInet
            {
                Family = (ushort)RouteAddressFamily.IPv6,
                IPv4Address = 0,
                IPv6Address = bytes
            };
        }

        throw new ArgumentException("Only IPv4 and IPv6 addresses are supported.", nameof(address));
    }

    public IPAddress ToIPAddress()
    {
        if (Family == (ushort)RouteAddressFamily.IPv4)
            return new IPAddress(BitConverter.GetBytes(IPv4Address));

        if (Family == (ushort)RouteAddressFamily.IPv6 && IPv6Address is { Length: 16 })
            return new IPAddress(IPv6Address);

        throw new InvalidOperationException("The native socket address has an unsupported family or invalid storage.");
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeIpAddressPrefix
{
    public NativeSockaddrInet Prefix;
    public byte PrefixLength;
    private byte Padding0;
    private byte Padding1;
    private byte Padding2;

    public static NativeIpAddressPrefix FromAddress(IPAddress address, byte prefixLength)
    {
        ValidatePrefix(address, prefixLength);
        return new NativeIpAddressPrefix
        {
            Prefix = NativeSockaddrInet.FromAddress(address),
            PrefixLength = prefixLength,
            Padding0 = 0,
            Padding1 = 0,
            Padding2 = 0
        };
    }

    public IPAddress Address => Prefix.ToIPAddress();

    private static void ValidatePrefix(IPAddress address, byte prefixLength)
    {
        var maximum = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength > maximum)
            throw new ArgumentOutOfRangeException(nameof(prefixLength), "The prefix length is invalid for the address family.");
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMibIpForwardRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public NativeIpAddressPrefix DestinationPrefix;
    public NativeSockaddrInet NextHop;
    public uint SitePrefixLength;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public uint Metric;
    public uint Protocol;
    [MarshalAs(UnmanagedType.U1)] public bool Loopback;
    [MarshalAs(UnmanagedType.U1)] public bool AutoconfigureAddress;
    [MarshalAs(UnmanagedType.U1)] public bool Publish;
    [MarshalAs(UnmanagedType.U1)] public bool Immortal;
    public uint Age;
    public uint Origin;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct NativeMibIpForwardTable2
{
    public uint NumEntries;
    private uint AlignmentPadding;
}

internal static class WindowsRouteNativeTranslation
{
    private const uint MibIpForwardProtoNetMgmt = 3;
    private const uint MibIpForwardOriginManual = 2;

    public static NativeMibIpForwardRow2 ToNativeRow(RouteRestorationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.InterfaceIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.InterfaceIndex));

        if (!IPAddress.TryParse(command.DestinationAddress, out var destination) ||
            !IPAddress.TryParse(command.NextHop, out var nextHop))
            throw new ArgumentException("The route contains an invalid destination or next hop.", nameof(command));

        if (destination.AddressFamily != nextHop.AddressFamily)
            throw new ArgumentException("The route destination and next hop families must match.", nameof(command));

        if ((command.AddressFamily == RouteAddressFamily.IPv4 && destination.AddressFamily != AddressFamily.InterNetwork) ||
            (command.AddressFamily == RouteAddressFamily.IPv6 && destination.AddressFamily != AddressFamily.InterNetworkV6))
            throw new ArgumentException("The route address family does not match the addresses.", nameof(command));

        var row = default(NativeMibIpForwardRow2);
        PopulateNativeRow(ref row, command, destination, nextHop);

        return row;
    }

    public static void PopulateNativeRow(
        ref NativeMibIpForwardRow2 row,
        RouteRestorationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.InterfaceIndex <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.InterfaceIndex));

        if (!IPAddress.TryParse(command.DestinationAddress, out var destination) ||
            !IPAddress.TryParse(command.NextHop, out var nextHop))
            throw new ArgumentException("The route contains an invalid destination or next hop.", nameof(command));

        if (destination.AddressFamily != nextHop.AddressFamily)
            throw new ArgumentException("The route destination and next hop families must match.", nameof(command));

        if ((command.AddressFamily == RouteAddressFamily.IPv4 && destination.AddressFamily != AddressFamily.InterNetwork) ||
            (command.AddressFamily == RouteAddressFamily.IPv6 && destination.AddressFamily != AddressFamily.InterNetworkV6))
            throw new ArgumentException("The route address family does not match the addresses.", nameof(command));

        PopulateNativeRow(ref row, command, destination, nextHop);
    }

    private static void PopulateNativeRow(
        ref NativeMibIpForwardRow2 row,
        RouteRestorationCommand command,
        IPAddress destination,
        IPAddress nextHop)
    {
        row.InterfaceIndex = checked((uint)command.InterfaceIndex);
        row.DestinationPrefix = NativeIpAddressPrefix.FromAddress(destination, command.PrefixLength);
        row.NextHop = NativeSockaddrInet.FromAddress(nextHop);
        row.SitePrefixLength = command.PrefixLength;
        row.ValidLifetime = uint.MaxValue;
        row.PreferredLifetime = uint.MaxValue;
        row.Metric = command.Metric;
        row.Protocol = MibIpForwardProtoNetMgmt;
        row.Loopback = false;
        row.AutoconfigureAddress = false;
        row.Publish = false;
        row.Immortal = false;
        row.Age = 0;
        row.Origin = MibIpForwardOriginManual;
    }

    public static RouteState ToRouteState(NativeMibIpForwardRow2 row)
        => new()
        {
            Destination = $"{row.DestinationPrefix.Address}/{row.DestinationPrefix.PrefixLength}",
            NextHop = row.NextHop.ToIPAddress().ToString(),
            InterfaceIndex = checked((int)row.InterfaceIndex),
            Metric = row.Metric,
            AddressFamily = row.DestinationPrefix.Prefix.Family.ToString()
        };
}

internal interface IWindowsRouteNativeApi
{
    void InitializeIpForwardEntry(ref NativeMibIpForwardRow2 row);
    uint CreateIpForwardEntry2(ref NativeMibIpForwardRow2 row);
    uint DeleteIpForwardEntry2(ref NativeMibIpForwardRow2 row);
    uint GetIpForwardTable2(ushort addressFamily, out IntPtr table);
    void FreeMibTable(IntPtr table);
}

internal sealed class WindowsRouteMutationNative : IRouteMutationNative
{
    private readonly IWindowsRouteNativeApi _api;

    internal WindowsRouteMutationNative()
        : this(new WindowsRouteNativeApi())
    {
    }

    internal WindowsRouteMutationNative(IWindowsRouteNativeApi api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public Task<RouteQueryResult> QueryAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        NativeMibIpForwardTable2 table = default;
        var status = _api.GetIpForwardTable2((ushort)command.AddressFamily, out var tablePointer);
        var mappedStatus = WindowsRouteNativeStatusMapper.Map(status);
        if (mappedStatus != RouteNativeStatus.Success)
            return Task.FromResult(new RouteQueryResult(false, null, mappedStatus));

        try
        {
            table = Marshal.PtrToStructure<NativeMibIpForwardTable2>(tablePointer);
            var rowSize = Marshal.SizeOf<NativeMibIpForwardRow2>();
            var headerSize = Marshal.SizeOf<NativeMibIpForwardTable2>();
            RouteState? conflictingRoute = null;
            for (uint index = 0; index < table.NumEntries; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowPointer = tablePointer + checked((int)(headerSize + index * (uint)rowSize));
                var row = Marshal.PtrToStructure<NativeMibIpForwardRow2>(rowPointer);
                var state = WindowsRouteNativeTranslation.ToRouteState(row);
                if (StringComparer.OrdinalIgnoreCase.Equals(state.Destination, command.Destination) &&
                    StringComparer.Ordinal.Equals(state.NextHop, command.NextHop) &&
                    state.InterfaceIndex == command.InterfaceIndex &&
                    state.Metric == command.Metric)
                    return Task.FromResult(new RouteQueryResult(true, state));

                if (StringComparer.OrdinalIgnoreCase.Equals(state.Destination, command.Destination))
                    conflictingRoute ??= state;
            }

            if (conflictingRoute is not null)
                return Task.FromResult(new RouteQueryResult(true, conflictingRoute));

            return Task.FromResult(new RouteQueryResult(false, null));
        }
        finally
        {
            _api.FreeMibTable(tablePointer);
        }
    }

    public Task<bool> AddRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        => MutateAsync(command, cancellationToken, static (api, ref row) => api.CreateIpForwardEntry2(ref row));

    public Task<bool> DeleteRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        => MutateAsync(command, cancellationToken, static (api, ref row) => api.DeleteIpForwardEntry2(ref row));

    private async Task<bool> MutateAsync(
        RouteRestorationCommand command,
        CancellationToken cancellationToken,
        NativeMutation mutation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = default(NativeMibIpForwardRow2);
        _api.InitializeIpForwardEntry(ref row);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsRouteNativeTranslation.PopulateNativeRow(ref row, command);
        var status = mutation(_api, ref row);
        await Task.CompletedTask.ConfigureAwait(false);
        return WindowsRouteNativeStatusMapper.Map(status) == RouteNativeStatus.Success;
    }

    private delegate uint NativeMutation(IWindowsRouteNativeApi api, ref NativeMibIpForwardRow2 row);
}

internal sealed class WindowsRouteNativeApi : IWindowsRouteNativeApi
{
    public void InitializeIpForwardEntry(ref NativeMibIpForwardRow2 row)
        => NativeMethods.InitializeIpForwardEntry(ref row);

    public uint CreateIpForwardEntry2(ref NativeMibIpForwardRow2 row)
        => NativeMethods.CreateIpForwardEntry2(ref row);

    public uint DeleteIpForwardEntry2(ref NativeMibIpForwardRow2 row)
        => NativeMethods.DeleteIpForwardEntry2(ref row);

    public uint GetIpForwardTable2(ushort addressFamily, out IntPtr table)
        => NativeMethods.GetIpForwardTable2(addressFamily, out table);

    public void FreeMibTable(IntPtr table)
        => NativeMethods.FreeMibTable(table);
}

internal static class NativeMethods
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void InitializeIpForwardEntry(ref NativeMibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint CreateIpForwardEntry2(ref NativeMibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint DeleteIpForwardEntry2(ref NativeMibIpForwardRow2 row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpForwardTable2(ushort addressFamily, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr memory);
}
