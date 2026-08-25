using System.Net;
using System.Runtime.InteropServices;
using LibertyRoute.Core;

namespace LibertyRoute.Networking.Native;

internal static class WindowsRouteTableReader
{
    private const int AddressFamilyInterNetwork = 2;
    private const int AddressFamilyInterNetworkV6 = 23;
    private const int ErrorSuccess = 0;

    public static IReadOnlyList<RouteState> Capture()
    {
        var result = new List<RouteState>();

        foreach (var addressFamily in new[] { AddressFamilyInterNetwork, AddressFamilyInterNetworkV6 })
        {
            var status = GetIpForwardTable2(addressFamily, out var table);
            if (status != ErrorSuccess)
            {
                continue;
            }

            try
            {
                var headerSize = Marshal.SizeOf<MibIpForwardTable2>();
                var rowSize = Marshal.SizeOf<MibIpForwardRow2>();
                var tableHeader = Marshal.PtrToStructure<MibIpForwardTable2>(table);

                for (uint index = 0; index < tableHeader.NumEntries; index++)
                {
                    var rowOffset = checked((int)(index * (uint)rowSize));
                    var row = Marshal.PtrToStructure<MibIpForwardRow2>(table + headerSize + rowOffset);
                    result.Add(new RouteState
                    {
                        Destination = $"{row.DestinationPrefix.Address}:{row.DestinationPrefix.PrefixLength}",
                        NextHop = row.NextHop.ToString(),
                        InterfaceIndex = checked((int)row.InterfaceIndex),
                        Metric = row.Metric,
                        AddressFamily = row.DestinationPrefix.AddressFamily.ToString()
                    });
                }
            }
            finally
            {
                FreeMibTable(table);
            }
        }

        return result
            .OrderBy(route => route.AddressFamily, StringComparer.Ordinal)
            .ThenBy(route => route.Destination, StringComparer.Ordinal)
            .ThenBy(route => route.InterfaceIndex)
            .ToArray();
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpForwardTable2(int addressFamily, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardTable2
    {
        public uint NumEntries;
        private uint TableAlignmentPadding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow2
    {
        private ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        private uint SitePrefixLength;
        private uint ValidLifetime;
        private uint PreferredLifetime;
        public uint Metric;
        private int Protocol;
        private byte Loopback;
        private byte AutoconfigureAddress;
        private byte Publish;
        private byte Immortal;
        private uint Age;
        private int Origin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte PrefixLength;

        public IPAddress Address => Prefix.Address;
        public int AddressFamily => Prefix.AddressFamily;
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)]
        private short Family;

        [FieldOffset(4)]
        private uint IPv4Address;

        [FieldOffset(8)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        private byte[]? IPv6Address;

        public int AddressFamily => Family == AddressFamilyInterNetwork ? AddressFamilyInterNetwork : AddressFamilyInterNetworkV6;

        public IPAddress Address
        {
            get
            {
                if (AddressFamily == AddressFamilyInterNetwork)
                {
                    return new IPAddress(BitConverter.GetBytes(IPv4Address));
                }

                return new IPAddress(IPv6Address ?? new byte[16]);
            }
        }

        public override string ToString() => Address.ToString();
    }
}