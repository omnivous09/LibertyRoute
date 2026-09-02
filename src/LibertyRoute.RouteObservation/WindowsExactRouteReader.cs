using System.Net;
using System.Runtime.InteropServices;
using LibertyRoute.Core;

namespace LibertyRoute.RouteObservation;

[StructLayout(LayoutKind.Explicit, Size = 28, Pack = 4)]
public struct ExactNativeSockaddrInet
{
    [FieldOffset(0)] public ushort Family;
    [FieldOffset(4)] public uint Ipv4Address;
    [FieldOffset(8), MarshalAs(UnmanagedType.ByValArray, SizeConst = 16, ArraySubType = UnmanagedType.U1)]
    public byte[]? Ipv6Address;
    [FieldOffset(24)] public uint Ipv6ScopeId;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ExactNativeIpAddressPrefix
{
    public ExactNativeSockaddrInet Prefix;
    public byte PrefixLength;
    private byte Padding0;
    private byte Padding1;
    private byte Padding2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ExactNativeMibIpForwardRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public ExactNativeIpAddressPrefix DestinationPrefix;
    public ExactNativeSockaddrInet NextHop;
    public byte SitePrefixLength;
    private byte Padding0;
    private byte Padding1;
    private byte Padding2;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public uint Metric;
    public uint Protocol;
    public byte Loopback;
    public byte AutoconfigureAddress;
    public byte Publish;
    public byte Immortal;
    public uint Age;
    public uint Origin;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ExactNativeMibIpForwardTable2Header
{
    public uint NumEntries;
    private uint AlignmentPadding;
}

public static class NativeRouteAbi
{
    public static int SockaddrSize => Marshal.SizeOf<ExactNativeSockaddrInet>();
    public static int PrefixSize => Marshal.SizeOf<ExactNativeIpAddressPrefix>();
    public static int RowSize => Marshal.SizeOf<ExactNativeMibIpForwardRow2>();
    public static int TableFirstRowOffset => Marshal.SizeOf<ExactNativeMibIpForwardTable2Header>();
    public static int OffsetOfRowField(string name) => checked((int)Marshal.OffsetOf<ExactNativeMibIpForwardRow2>(name));
}

public static class NativeRouteDecoder
{
    public static ExactNativeRouteRow DecodeRowPointer(IntPtr rowPointer, NativeRouteAddressFamily expectedFamily)
    {
        if (rowPointer == IntPtr.Zero) throw new ArgumentException("Native row pointer is null.", nameof(rowPointer));
        return Decode(Marshal.PtrToStructure<ExactNativeMibIpForwardRow2>(rowPointer), expectedFamily);
    }

    public static ExactNativeRouteRow Decode(ExactNativeMibIpForwardRow2 row, NativeRouteAddressFamily expectedFamily)
    {
        if (row.InterfaceLuid == 0 || row.InterfaceIndex == 0)
            throw new InvalidOperationException("Native route interface identity is unavailable.");
        var destination = DecodeAddress(row.DestinationPrefix.Prefix, expectedFamily, preserveScope: false);
        var nextHop = DecodeAddress(row.NextHop, expectedFamily, preserveScope: true);
        var maximumPrefix = expectedFamily == NativeRouteAddressFamily.IPv4 ? 32 : 128;
        if (row.DestinationPrefix.PrefixLength > maximumPrefix || row.SitePrefixLength > maximumPrefix)
            throw new InvalidOperationException("Native route prefix evidence is invalid.");
        if (row.PreferredLifetime > row.ValidLifetime)
            throw new InvalidOperationException("Native route lifetime evidence is invalid.");

        var key = NativeRouteKey.Create(expectedFamily, destination, row.DestinationPrefix.PrefixLength,
            nextHop, row.InterfaceLuid);
        var profile = new NativeRouteProfile(row.SitePrefixLength, row.ValidLifetime,
            row.PreferredLifetime, row.Metric, row.Protocol, ToBoolean(row.Loopback),
            ToBoolean(row.AutoconfigureAddress), ToBoolean(row.Publish), ToBoolean(row.Immortal));
        return new ExactNativeRouteRow(new ExactRouteMutationIdentity(
            ExactRouteMutationIdentity.CurrentSchemaVersion, key, profile), row.InterfaceIndex, row.Age, row.Origin);
    }

    private static IPAddress DecodeAddress(
        ExactNativeSockaddrInet socket, NativeRouteAddressFamily expectedFamily, bool preserveScope)
    {
        if (socket.Family != (ushort)expectedFamily)
            throw new InvalidOperationException("Native socket family does not match the requested route family.");
        if (expectedFamily == NativeRouteAddressFamily.IPv4)
            return new IPAddress(BitConverter.GetBytes(socket.Ipv4Address));
        if (socket.Ipv6Address is not { Length: 16 })
            throw new InvalidOperationException("Native IPv6 socket storage is malformed.");
        return new IPAddress(socket.Ipv6Address, preserveScope ? socket.Ipv6ScopeId : 0);
    }

    private static bool ToBoolean(byte value) => value switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidOperationException("Native BOOLEAN evidence is malformed.")
    };
}

public static class WindowsExactRouteReader
{
    public const int MaximumRoutes = 4096;

    public readonly record struct NativeCountAssessment(bool CanMaterialize, int RowsToRead, bool Truncated);

    public static NativeCountAssessment AssessNativeCount(uint nativeCount, int remainingCapacity)
    {
        if (remainingCapacity < 0) throw new ArgumentOutOfRangeException(nameof(remainingCapacity));
        return nativeCount > (uint)remainingCapacity
            ? new(false, 0, true)
            : new(true, checked((int)nativeCount), false);
    }

    public static ExactRouteObservation Observe() => Observe(MaximumRoutes);

    internal static ExactRouteObservation Observe(int maximumRoutes)
    {
        var rows = new List<ExactNativeRouteRow>(Math.Min(maximumRoutes, 256));
        var reasons = new List<string>();
        var ipv4 = ObserveFamily(NativeRouteAddressFamily.IPv4, maximumRoutes - rows.Count, rows, reasons);
        var ipv6 = ObserveFamily(NativeRouteAddressFamily.IPv6, maximumRoutes - rows.Count, rows, reasons);
        return new ExactRouteObservation(rows.ToArray(), ipv4.Complete, ipv6.Complete,
            ipv4.Truncated || ipv6.Truncated, reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static FamilyResult ObserveFamily(
        NativeRouteAddressFamily family, int remainingCapacity,
        ICollection<ExactNativeRouteRow> rows, ICollection<string> reasons)
    {
        var status = ExactRouteNativeMethods.GetIpForwardTable2((int)family, out var table);
        if (status != 0)
        {
            reasons.Add($"{family} exact route enumeration failed with native status {status}.");
            return new(false, false);
        }
        if (table == IntPtr.Zero)
        {
            reasons.Add($"{family} exact route enumeration returned no table.");
            return new(false, false);
        }

        try
        {
            var header = Marshal.PtrToStructure<ExactNativeMibIpForwardTable2Header>(table);
            var countAssessment = AssessNativeCount(header.NumEntries, Math.Max(remainingCapacity, 0));
            if (countAssessment.Truncated)
            {
                reasons.Add($"Exact route observation exceeds the {MaximumRoutes}-row limit.");
                return new(true, true);
            }

            var count = countAssessment.RowsToRead;
            for (var index = 0; index < count; index++)
            {
                try
                {
                    var offset = checked(NativeRouteAbi.TableFirstRowOffset + checked(index * NativeRouteAbi.RowSize));
                    rows.Add(NativeRouteDecoder.DecodeRowPointer(table + offset, family));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
                {
                    reasons.Add($"{family} exact route enumeration contained malformed evidence.");
                    return new(false, false);
                }
            }
            return new(true, false);
        }
        finally
        {
            ExactRouteNativeMethods.FreeMibTable(table);
        }
    }

    private readonly record struct FamilyResult(bool Complete, bool Truncated);
}

internal static class ExactRouteNativeMethods
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int GetIpForwardTable2(int addressFamily, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr memory);
}
