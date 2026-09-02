using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LibertyRoute.RouteObservation;

public static class Program
{
    internal const int MaxRoutes = 4096;
    internal const int MaxInterfaces = 256;
    internal const int MaxValuesPerInterface = 64;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ObservationOptions.Parse(args);
            var routeResult = StrictWindowsRouteObserver.Observe(MaxRoutes);
            var interfaceResult = WindowsInterfaceObserver.Observe(
                routeResult.Routes, MaxInterfaces, MaxValuesPerInterface);
            var inputs = ObservationAssembler.Create(routeResult, interfaceResult, options);
            var decision = RouteCandidatePolicy.Select(inputs);
            var report = RouteObservationReport.Create(inputs, decision);
            var json = JsonSerializer.Serialize(report, JsonOptions);
            Console.WriteLine(json);

            if (options.OutputPath is not null)
                await AtomicEvidenceWriter.WriteAsync(options.OutputPath, json);

            if (!decision.SafeCandidateExists)
                Console.Error.WriteLine("NO SAFE 4N ROUTE TARGET ON THIS MACHINE");

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or NetworkInformationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record ObservationOptions(
    string? OutputPath,
    int? DedicatedInterfaceIndex,
    IReadOnlySet<int> ManagementInterfaceIndexes)
{
    public static ObservationOptions Parse(IReadOnlyList<string> args)
    {
        string? output = null;
        int? dedicated = null;
        var management = new HashSet<int>();
        for (var index = 0; index < args.Count; index++)
        {
            var option = args[index];
            if (option is not ("--output" or "--dedicated-interface" or "--management-interface"))
                throw new ArgumentException($"Unknown option: {option}");
            if (++index >= args.Count)
                throw new ArgumentException($"Missing value for {option}.");

            if (option == "--output")
            {
                output = Path.GetFullPath(args[index]);
            }
            else if (!int.TryParse(args[index], out var interfaceIndex) || interfaceIndex <= 0)
            {
                throw new ArgumentException($"{option} requires a positive numeric interface index.");
            }
            else if (option == "--dedicated-interface")
            {
                dedicated = interfaceIndex;
            }
            else
            {
                management.Add(interfaceIndex);
            }
        }

        return new ObservationOptions(output, dedicated, management);
    }
}

public sealed record CanonicalRouteObservation(
    string AddressFamily,
    string Destination,
    int PrefixLength,
    string NextHop,
    int InterfaceIndex,
    uint RouteMetric);

public sealed record InterfaceAddressObservation(string Address, int PrefixLength);

public sealed record InterfaceObservation(
    int InterfaceIndex,
    string InterfaceId,
    string Name,
    string InterfaceType,
    string OperationalStatus,
    IReadOnlyList<InterfaceAddressObservation> Ipv4Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers,
    bool HasIpv4DefaultRoute,
    bool HasIpv6DefaultRoute);

public sealed record StrictRouteObservationResult(
    IReadOnlyList<CanonicalRouteObservation> Routes,
    bool Ipv4Complete,
    bool Ipv6Complete,
    bool Truncated,
    IReadOnlyList<string> IncompleteReasons)
{
    public bool Complete => Ipv4Complete && Ipv6Complete && !Truncated && IncompleteReasons.Count == 0;
}

public sealed record StrictInterfaceObservationResult(
    IReadOnlyList<InterfaceObservation> Interfaces,
    bool Complete,
    bool Truncated,
    IReadOnlyList<string> IncompleteReasons);

public sealed record RouteObservationInputs(
    IReadOnlyList<CanonicalRouteObservation> Routes,
    IReadOnlyList<InterfaceObservation> Interfaces,
    int? DedicatedInterfaceIndex,
    IReadOnlySet<int> ManagementInterfaceIndexes,
    bool Ipv4RouteObservationComplete,
    bool Ipv6RouteObservationComplete,
    bool RouteObservationComplete,
    bool InterfaceObservationComplete,
    bool ObservationTruncated,
    bool ObservationComplete,
    IReadOnlyList<string> IncompleteReasons);

public sealed record CandidateDecision(
    bool SafeCandidateExists,
    string? Target,
    int? InterfaceIndex,
    int TargetMatchCount,
    IReadOnlyList<string> ExclusionReasons);

public static class ObservationAssembler
{
    public static RouteObservationInputs Create(
        StrictRouteObservationResult routes,
        StrictInterfaceObservationResult interfaces,
        ObservationOptions options)
    {
        var reasons = routes.IncompleteReasons.Concat(interfaces.IncompleteReasons)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var truncated = routes.Truncated || interfaces.Truncated;
        var complete = routes.Complete && interfaces.Complete && !truncated && reasons.Length == 0;
        return new RouteObservationInputs(
            CanonicalOrdering.Routes(routes.Routes),
            interfaces.Interfaces.OrderBy(item => item.InterfaceIndex).ThenBy(item => item.InterfaceId, StringComparer.Ordinal).ToArray(),
            options.DedicatedInterfaceIndex,
            options.ManagementInterfaceIndexes,
            routes.Ipv4Complete,
            routes.Ipv6Complete,
            routes.Complete,
            interfaces.Complete,
            truncated,
            complete,
            reasons);
    }
}

public static class RouteCandidatePolicy
{
    private static readonly byte[][] TestNetPrefixes = [[192, 0, 2], [198, 51, 100], [203, 0, 113]];

    public static bool IsPermittedTargetClass(IPAddress destination, int prefixLength) =>
        destination.AddressFamily == AddressFamily.InterNetwork && prefixLength == 32 &&
        TestNetPrefixes.Any(prefix => destination.GetAddressBytes().AsSpan(0, 3).SequenceEqual(prefix));

    public static CandidateDecision Select(RouteObservationInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.ObservationComplete)
        {
            var reasons = input.IncompleteReasons.Count == 0
                ? ["Observation is incomplete; candidate approval is prohibited."]
                : input.IncompleteReasons;
            return Reject(reasons);
        }

        var reasonsSet = new SortedSet<string>(StringComparer.Ordinal);
        if (input.DedicatedInterfaceIndex is null)
        {
            reasonsSet.Add("No dedicated non-management interface was explicitly supplied.");
            return Reject(reasonsSet);
        }

        var matchingInterfaces = input.Interfaces
            .Where(candidate => candidate.InterfaceIndex == input.DedicatedInterfaceIndex.Value).ToArray();
        if (matchingInterfaces.Length != 1)
        {
            reasonsSet.Add("Dedicated interface identity is missing or ambiguous.");
            return Reject(reasonsSet);
        }

        var selectedInterface = matchingInterfaces[0];
        ValidateInterface(selectedInterface, input.ManagementInterfaceIndexes, reasonsSet);
        if (reasonsSet.Count > 0) return Reject(reasonsSet);

        foreach (var prefix in TestNetPrefixes)
        {
            for (var host = 254; host >= 1; host--)
            {
                var target = new IPAddress([prefix[0], prefix[1], prefix[2], (byte)host]);
                if (CountTargetMatches(target, input.Routes) != 0 || CollidesWithInterfaceEvidence(target, input.Interfaces))
                    continue;
                return new CandidateDecision(true, $"{target}/32", selectedInterface.InterfaceIndex, 0, []);
            }
        }

        return Reject(["Every TEST-NET /32 target collides with observed route or interface evidence."]);
    }

    public static CandidateDecision EvaluateTarget(RouteObservationInputs input, IPAddress target, int prefixLength)
    {
        if (!IsPermittedTargetClass(target, prefixLength))
            return new CandidateDecision(false, null, input.DedicatedInterfaceIndex, 0, ["Target must be a TEST-NET IPv4 /32."]);

        var baseDecision = Select(input);
        var matches = CountTargetMatches(target, input.Routes);
        if (!baseDecision.SafeCandidateExists) return baseDecision with { TargetMatchCount = matches };
        if (matches > 0)
            return new CandidateDecision(false, null, baseDecision.InterfaceIndex, matches,
                [matches > 1 ? "Target has duplicate route observations." : "Target already exists in the route table."]);
        if (CollidesWithInterfaceEvidence(target, input.Interfaces))
            return new CandidateDecision(false, null, baseDecision.InterfaceIndex, 0,
                ["Target collides with a local address, subnet, gateway, or DNS server."]);
        return baseDecision with { Target = $"{target}/32" };
    }

    private static int CountTargetMatches(IPAddress target, IEnumerable<CanonicalRouteObservation> routes) =>
        routes.Count(route => route.AddressFamily == AddressFamily.InterNetwork.ToString() &&
            IPAddress.TryParse(route.Destination, out var destination) && destination.Equals(target));

    private static void ValidateInterface(InterfaceObservation candidate, IReadOnlySet<int> management, ISet<string> reasons)
    {
        if (!string.Equals(candidate.OperationalStatus, OperationalStatus.Up.ToString(), StringComparison.Ordinal))
            reasons.Add("Dedicated interface is not Up.");
        if (management.Contains(candidate.InterfaceIndex)) reasons.Add("Dedicated interface is an identified management path.");
        if (candidate.HasIpv4DefaultRoute || candidate.HasIpv6DefaultRoute) reasons.Add("Dedicated interface carries a default route.");
        if (candidate.Gateways.Count > 0) reasons.Add("Dedicated interface carries gateway reachability.");
        if (candidate.DnsServers.Count > 0) reasons.Add("Dedicated interface carries DNS reachability.");
        if (candidate.InterfaceType is "Loopback" or "Tunnel" or "Ppp") reasons.Add("Dedicated interface type is not eligible.");
    }

    private static bool CollidesWithInterfaceEvidence(IPAddress target, IEnumerable<InterfaceObservation> interfaces)
    {
        foreach (var item in interfaces)
        {
            if (item.Gateways.Concat(item.DnsServers).Any(value =>
                    IPAddress.TryParse(value, out var address) && address.Equals(target))) return true;
            foreach (var local in item.Ipv4Addresses)
            {
                if (!IPAddress.TryParse(local.Address, out var address)) return true;
                if (address.Equals(target) || IsInSubnet(target, address, local.PrefixLength)) return true;
            }
        }
        return false;
    }

    private static bool IsInSubnet(IPAddress candidate, IPAddress networkAddress, int prefixLength)
    {
        if (prefixLength is < 0 or > 32) return true;
        var candidateBytes = candidate.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (!candidateBytes.AsSpan(0, fullBytes).SequenceEqual(networkBytes.AsSpan(0, fullBytes))) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (candidateBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }

    private static CandidateDecision Reject(IEnumerable<string> reasons) =>
        new(false, null, null, 0, reasons.Order(StringComparer.Ordinal).ToArray());
}

internal static class StrictWindowsRouteObserver
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    public static StrictRouteObservationResult Observe(int maximumRoutes)
    {
        var routes = new List<CanonicalRouteObservation>(Math.Min(maximumRoutes, 256));
        var reasons = new List<string>();
        var ipv4 = ObserveFamily(AfInet, "IPv4", maximumRoutes - routes.Count, routes, reasons);
        var ipv6 = ObserveFamily(AfInet6, "IPv6", maximumRoutes - routes.Count, routes, reasons);
        return new StrictRouteObservationResult(CanonicalOrdering.Routes(routes), ipv4.Complete, ipv6.Complete,
            ipv4.Truncated || ipv6.Truncated, reasons.Order(StringComparer.Ordinal).ToArray());
    }

    private static FamilyResult ObserveFamily(
        int family, string label, int remainingCapacity, ICollection<CanonicalRouteObservation> routes, ICollection<string> reasons)
    {
        var status = ExactRouteNativeMethods.GetIpForwardTable2(family, out var table);
        if (status != 0)
        {
            reasons.Add($"{label} route enumeration failed with native status {status}.");
            return new FamilyResult(false, false);
        }
        if (table == IntPtr.Zero)
        {
            reasons.Add($"{label} route enumeration returned no table.");
            return new FamilyResult(false, false);
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            if (count < 0)
            {
                reasons.Add($"{label} route enumeration returned an invalid row count.");
                return new FamilyResult(false, false);
            }

            var truncated = count > remainingCapacity;
            var rowsToRead = Math.Min(count, Math.Max(remainingCapacity, 0));
            var rowSize = Marshal.SizeOf<MibIpForwardRow2>();
            var headerSize = Marshal.SizeOf<MibIpForwardTable2>();
            for (var index = 0; index < rowsToRead; index++)
            {
                try
                {
                    var offset = checked(headerSize + checked(index * rowSize));
                    var row = Marshal.PtrToStructure<MibIpForwardRow2>(table + offset);
                    routes.Add(Translate(row, family));
                }
                catch (Exception exception) when (exception is OverflowException or ArgumentException or InvalidOperationException)
                {
                    reasons.Add($"{label} route enumeration contained malformed row data.");
                    return new FamilyResult(false, truncated);
                }
            }

            if (truncated) reasons.Add($"Route observation exceeds the {Program.MaxRoutes}-row limit.");
            return new FamilyResult(true, truncated);
        }
        finally
        {
            ExactRouteNativeMethods.FreeMibTable(table);
        }
    }

    private static CanonicalRouteObservation Translate(MibIpForwardRow2 row, int expectedFamily)
    {
        var destination = row.DestinationPrefix.Prefix.GetAddress(expectedFamily);
        var nextHop = row.NextHop.GetAddress(expectedFamily);
        var maxPrefix = expectedFamily == AfInet ? 32 : 128;
        if (row.DestinationPrefix.PrefixLength > maxPrefix || row.InterfaceIndex == 0)
            throw new InvalidOperationException("Malformed native route row.");
        return new CanonicalRouteObservation(destination.AddressFamily.ToString(), destination.ToString(),
            row.DestinationPrefix.PrefixLength, nextHop.ToString(), checked((int)row.InterfaceIndex), row.Metric);
    }

    private readonly record struct FamilyResult(bool Complete, bool Truncated);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardTable2
    {
        public uint NumEntries;
        private uint AlignmentPadding;
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
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)] private short Family;
        [FieldOffset(4)] private uint Ipv4Address;
        [FieldOffset(8), MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] private byte[]? Ipv6Address;
        [FieldOffset(24)] private uint Ipv6ScopeId;

        public IPAddress GetAddress(int expectedFamily)
        {
            if (Family != expectedFamily) throw new InvalidOperationException("Native route family mismatch.");
            return Family == AfInet
                ? new IPAddress(BitConverter.GetBytes(Ipv4Address))
                : new IPAddress(Ipv6Address ?? throw new InvalidOperationException("Missing IPv6 address."), Ipv6ScopeId);
        }
    }
}

internal static class WindowsInterfaceObserver
{
    public static StrictInterfaceObservationResult Observe(
        IReadOnlyList<CanonicalRouteObservation> routes, int maximumInterfaces, int maximumValues)
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return new StrictInterfaceObservationResult([], false, false, ["Interface enumeration failed."]);
        }

        var truncated = all.Length > maximumInterfaces;
        var reasons = new List<string>();
        if (truncated) reasons.Add($"Interface observation exceeds the {maximumInterfaces}-interface limit.");
        var observations = new List<InterfaceObservation>(Math.Min(all.Length, maximumInterfaces));
        foreach (var networkInterface in all.Take(maximumInterfaces))
        {
            var capture = Capture(networkInterface, routes, maximumValues);
            if (capture.Observation is not null) observations.Add(capture.Observation);
            reasons.AddRange(capture.IncompleteReasons);
            truncated |= capture.Truncated;
        }

        var complete = !truncated && reasons.Count == 0 && observations.Count == all.Length;
        return new StrictInterfaceObservationResult(observations, complete, truncated,
            reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static InterfaceCapture Capture(
        NetworkInterface networkInterface, IReadOnlyList<CanonicalRouteObservation> routes, int maximumValues)
    {
        var reasons = new List<string>();
        IPInterfaceProperties properties;
        try { properties = networkInterface.GetIPProperties(); }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            return new InterfaceCapture(null, false, ["Interface properties could not be read."]);
        }

        var ipv4 = ReadProperties(properties.GetIPv4Properties, "IPv4", reasons);
        var ipv6 = ReadProperties(properties.GetIPv6Properties, "IPv6", reasons);
        var index = ipv4?.Index ?? ipv6?.Index;
        if (index is null || index <= 0 || string.IsNullOrWhiteSpace(networkInterface.Id))
        {
            reasons.Add("Interface identity or index is unavailable.");
            return new InterfaceCapture(null, false, reasons);
        }

        var addresses = ReadBounded(properties.UnicastAddresses
            .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => new InterfaceAddressObservation(item.Address.ToString(), item.PrefixLength)),
            maximumValues, "local addresses", reasons);
        var gateways = ReadBounded(properties.GatewayAddresses
            .Select(item => item.Address).Where(item => item.AddressFamily == AddressFamily.InterNetwork)
            .Select(item => item.ToString()), maximumValues, "gateways", reasons);
        var dns = ReadBounded(properties.DnsAddresses.Select(item => item.ToString()), maximumValues, "DNS servers", reasons);
        var truncated = addresses.Truncated || gateways.Truncated || dns.Truncated;

        var observation = new InterfaceObservation(index.Value, networkInterface.Id, networkInterface.Name,
            networkInterface.NetworkInterfaceType.ToString(), networkInterface.OperationalStatus.ToString(),
            addresses.Values.OrderBy(item => IPAddress.Parse(item.Address).GetAddressBytes(), ByteArrayComparer.Instance).ToArray(),
            gateways.Values.Order(StringComparer.Ordinal).ToArray(), dns.Values.Order(StringComparer.Ordinal).ToArray(),
            ipv4 is not null && HasDefault(routes, ipv4.Index, AddressFamily.InterNetwork),
            ipv6 is not null && HasDefault(routes, ipv6.Index, AddressFamily.InterNetworkV6));
        return new InterfaceCapture(observation, truncated, reasons);
    }

    private static T? ReadProperties<T>(Func<T> getter, string family, ICollection<string> reasons) where T : class
    {
        try
        {
            var value = getter();
            if (value is null) reasons.Add($"{family} interface properties returned no data.");
            return value;
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            reasons.Add($"{family} interface properties could not be read.");
            return null;
        }
    }

    private static BoundedValues<T> ReadBounded<T>(
        IEnumerable<T> source, int maximum, string label, ICollection<string> reasons)
    {
        try
        {
            var values = source.Take(maximum + 1).ToArray();
            var truncated = values.Length > maximum;
            if (truncated) reasons.Add($"Interface {label} exceed the {maximum}-value limit.");
            return new BoundedValues<T>(values.Take(maximum).ToArray(), truncated);
        }
        catch (Exception exception) when (IsExpectedReadFailure(exception))
        {
            reasons.Add($"Interface {label} could not be read.");
            return new BoundedValues<T>([], false);
        }
    }

    private static bool IsExpectedReadFailure(Exception exception) =>
        exception is NetworkInformationException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException;

    private static bool HasDefault(IEnumerable<CanonicalRouteObservation> routes, int index, AddressFamily family) =>
        routes.Any(route => route.InterfaceIndex == index && route.AddressFamily == family.ToString() && route.PrefixLength == 0);

    private sealed record InterfaceCapture(InterfaceObservation? Observation, bool Truncated, IReadOnlyList<string> IncompleteReasons);
    private sealed record BoundedValues<T>(IReadOnlyList<T> Values, bool Truncated);
}

public static class CanonicalOrdering
{
    public static IReadOnlyList<CanonicalRouteObservation> Routes(IEnumerable<CanonicalRouteObservation> routes) =>
        routes.OrderBy(route => route.AddressFamily, StringComparer.Ordinal)
            .ThenBy(route => IPAddress.Parse(route.Destination).GetAddressBytes(), ByteArrayComparer.Instance)
            .ThenBy(route => route.PrefixLength).ThenBy(route => route.InterfaceIndex)
            .ThenBy(route => route.NextHop, StringComparer.Ordinal).ThenBy(route => route.RouteMetric).ToArray();
}

public sealed record RouteObservationReport(
    int SchemaVersion,
    string EvidenceClassification,
    string AuthorityNotice,
    string ExpectedSourceCommit,
    DateTimeOffset CapturedAtUtc,
    string ToolVersion,
    string Platform,
    int RouteCount,
    bool Ipv4RouteObservationComplete,
    bool Ipv6RouteObservationComplete,
    bool RouteObservationComplete,
    bool InterfaceObservationComplete,
    bool ObservationTruncated,
    bool ObservationComplete,
    IReadOnlyList<string> IncompleteReasons,
    IReadOnlyList<CanonicalRouteObservation> Routes,
    IReadOnlyList<InterfaceObservation> Interfaces,
    IReadOnlyList<CanonicalRouteObservation> DefaultRoutes,
    CandidateDecision Candidate)
{
    public static RouteObservationReport Create(RouteObservationInputs inputs, CandidateDecision decision) => new(
        1, "REDUCED LIBERTYROUTE ROUTE STATE",
        "Operator evidence only; not D1 durable authority and not authorization to mutate networking.",
        "dd5a117c0198bbbb3e66cc1a8ea02660454415aa", DateTimeOffset.UtcNow,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        Environment.OSVersion.VersionString, inputs.Routes.Count,
        inputs.Ipv4RouteObservationComplete, inputs.Ipv6RouteObservationComplete,
        inputs.RouteObservationComplete, inputs.InterfaceObservationComplete,
        inputs.ObservationTruncated, inputs.ObservationComplete, inputs.IncompleteReasons,
        inputs.Routes, inputs.Interfaces, inputs.Routes.Where(route => route.PrefixLength == 0).ToArray(), decision);
}

public static class AtomicEvidenceWriter
{
    public static async Task WriteAsync(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Output directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal sealed class ByteArrayComparer : IComparer<byte[]>
{
    public static ByteArrayComparer Instance { get; } = new();
    public int Compare(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        return left.AsSpan().SequenceCompareTo(right);
    }
}
