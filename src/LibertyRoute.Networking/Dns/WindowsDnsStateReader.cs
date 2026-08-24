using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibertyRoute.Core;

namespace LibertyRoute.Networking.Dns;

internal static class WindowsDnsStateReader
{
    private const uint ErrorSuccess = 0;
    private const uint DnsInterfaceSettingsVersion1 = 1;
    private const int DnsSettingMaxNameServers = 10;

    public static DnsInterfaceState Capture(NetworkInterface networkInterface, IPInterfaceProperties properties)
    {
        var fallback = properties.DnsAddresses
            .Select(address => address.ToString())
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToArray();
        var configured = TryCaptureConfiguredServers(networkInterface.Id) ?? fallback;
        var ipv4DnsServers = configured
            .Where(address => IPAddress.TryParse(address, out var parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .ToArray();
        var ipv6DnsServers = configured
            .Where(address => IPAddress.TryParse(address, out var parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            .ToArray();

        return new DnsInterfaceState(
            networkInterface.Id,
            networkInterface.Name,
            networkInterface.OperationalStatus == OperationalStatus.Up,
            configured,
            ipv4DnsServers,
            ipv6DnsServers,
            DnsConfigurationSource.Unknown,
            DnsConfigurationSource.Unknown);
    }

    private static string[]? TryCaptureConfiguredServers(string interfaceId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(interfaceId, out var interfaceGuid))
            return null;

        var settings = new DnsInterfaceSettings
        {
            Version = DnsInterfaceSettingsVersion1,
            NameServer = Enumerable.Range(0, DnsSettingMaxNameServers)
                .Select(_ => new SockaddrStorage { Data = new byte[128] })
                .ToArray()
        };

        uint status;
        try
        {
            status = GetInterfaceDnsSettings(ref interfaceGuid, ref settings);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }

        if (status != ErrorSuccess)
            return null;

        try
        {
            var count = Math.Min(settings.NameServerCount, (uint)settings.NameServer.Length);
            return Enumerable.Range(0, checked((int)count))
                .Select(index => settings.NameServer[index].ToIPAddress())
                .Where(address => address is not null)
                .Select(address => address!.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            FreeInterfaceDnsSettings(ref settings);
        }
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint GetInterfaceDnsSettings(ref Guid interfaceGuid, ref DnsInterfaceSettings settings);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void FreeInterfaceDnsSettings(ref DnsInterfaceSettings settings);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsInterfaceSettings
    {
        public uint Version;
        public uint Flags;
        public IntPtr Domain;
        public IntPtr SearchList;
        public uint NameServerCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DnsSettingMaxNameServers)]
        public SockaddrStorage[] NameServer;
        public uint RegistrationEnabled;
        public uint UseSuffixWhenRegistering;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct SockaddrStorage
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] Data;

        public IPAddress? ToIPAddress()
        {
            if (Data is null || Data.Length < 24)
                return null;

            var family = BitConverter.ToInt16(Data, 0);
            return family switch
            {
                2 => new IPAddress(Data.Skip(4).Take(4).ToArray()),
                23 => new IPAddress(Data.Skip(8).Take(16).ToArray()),
                _ => null
            };
        }
    }
}