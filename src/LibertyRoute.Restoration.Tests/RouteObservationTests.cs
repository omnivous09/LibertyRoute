using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LibertyRoute.RouteObservation;

namespace LibertyRoute.Restoration.Tests;

public sealed class RouteObservationTests
{
    [Fact]
    public void TestNetIpv4Slash32IsAcceptedAsCandidateClass()
    {
        Assert.True(RouteCandidatePolicy.IsPermittedTargetClass(IPAddress.Parse("192.0.2.44"), 32));
        Assert.True(RouteCandidatePolicy.IsPermittedTargetClass(IPAddress.Parse("198.51.100.44"), 32));
        Assert.True(RouteCandidatePolicy.IsPermittedTargetClass(IPAddress.Parse("203.0.113.44"), 32));
    }

    [Fact]
    public void Slash24IsRejectedAsCandidateClass() =>
        Assert.False(RouteCandidatePolicy.IsPermittedTargetClass(IPAddress.Parse("192.0.2.0"), 24));

    [Fact]
    public void NonTestNetDestinationIsRejectedAsCandidateClass() =>
        Assert.False(RouteCandidatePolicy.IsPermittedTargetClass(IPAddress.Parse("8.8.8.8"), 32));

    [Fact]
    public void ExistingDestinationIsRejected()
    {
        var result = RouteCandidatePolicy.EvaluateTarget(Inputs(routes: [Route("192.0.2.44", 32)]),
            IPAddress.Parse("192.0.2.44"), 32);
        Assert.False(result.SafeCandidateExists);
        Assert.Equal(1, result.TargetMatchCount);
    }

    [Fact]
    public void DefaultRouteInterfaceIsRejected()
    {
        var result = RouteCandidatePolicy.Select(Inputs(@interface: EligibleInterface() with { HasIpv4DefaultRoute = true }));
        Assert.False(result.SafeCandidateExists);
        Assert.Contains(result.ExclusionReasons, reason => reason.Contains("default route", StringComparison.Ordinal));
    }

    [Fact]
    public void ManagementPathInterfaceIsRejected()
    {
        var result = RouteCandidatePolicy.Select(Inputs(management: new HashSet<int> { 42 }));
        Assert.False(result.SafeCandidateExists);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("gateway")]
    [InlineData("dns")]
    [InlineData("subnet")]
    public void LocalGatewayDnsAndSubnetCollisionsAreRejected(string collision)
    {
        var other = EligibleInterface(84);
        other = collision switch
        {
            "local" => other with { Ipv4Addresses = [new("192.0.2.44", 32)] },
            "subnet" => other with { Ipv4Addresses = [new("192.0.2.1", 24)] },
            "gateway" => other with { Gateways = ["192.0.2.44"] },
            _ => other with { DnsServers = ["192.0.2.44"] }
        };
        var result = RouteCandidatePolicy.EvaluateTarget(Inputs(interfaces: [EligibleInterface(), other]),
            IPAddress.Parse("192.0.2.44"), 32);
        Assert.False(result.SafeCandidateExists);
    }

    [Fact]
    public void DuplicateTargetObservationIsRejected()
    {
        var result = RouteCandidatePolicy.EvaluateTarget(
            Inputs(routes: [Route("203.0.113.9", 32), Route("203.0.113.9", 32, 84)]),
            IPAddress.Parse("203.0.113.9"), 32);
        Assert.False(result.SafeCandidateExists);
        Assert.Equal(2, result.TargetMatchCount);
    }

    [Fact]
    public void MissingDedicatedProofRejects()
    {
        Assert.False(RouteCandidatePolicy.Select(Inputs(dedicatedInterfaceIndex: null)).SafeCandidateExists);
    }

    [Fact]
    public void CanonicalRouteOrderingIsDeterministic()
    {
        var input = new[] { Route("203.0.113.2", 32, 7), Route("192.0.2.10", 32, 8), Route("192.0.2.2", 32, 9) };
        var first = CanonicalOrdering.Routes(input);
        var second = CanonicalOrdering.Routes(input.Reverse());
        Assert.Equal(first, second);
        Assert.Equal(["192.0.2.2", "192.0.2.10", "203.0.113.2"], first.Select(route => route.Destination));
    }

    [Fact]
    public void NoSafeTargetReturnsCleanRejection()
    {
        var routes = new List<CanonicalRouteObservation>();
        foreach (var prefix in new[] { "192.0.2", "198.51.100", "203.0.113" })
            for (var host = 1; host <= 254; host++) routes.Add(Route($"{prefix}.{host}", 32));
        Assert.False(RouteCandidatePolicy.Select(Inputs(routes: routes)).SafeCandidateExists);
    }

    [Fact]
    public void CandidateSelectionIsDeterministicAndBoundedToTestNet()
    {
        var result = RouteCandidatePolicy.Select(Inputs());
        Assert.True(result.SafeCandidateExists);
        Assert.Equal("192.0.2.254/32", result.Target);
        Assert.Equal(42, result.InterfaceIndex);
    }

    [Theory]
    [InlineData(false, true, "IPv4 route enumeration failed.")]
    [InlineData(true, false, "IPv6 route enumeration failed.")]
    [InlineData(false, false, "Both route families failed.")]
    public void RouteFamilyEnumerationFailureIsIncompleteAndRejects(bool ipv4Complete, bool ipv6Complete, string reason)
    {
        var routeResult = new StrictRouteObservationResult([], ipv4Complete, ipv6Complete, false, [reason]);
        AssertIncompleteAndRejected(routeResult, CompleteInterfaces());
    }

    [Fact]
    public void RouteCountOverLimitIsTruncatedIncompleteAndRejects()
    {
        var routes = Enumerable.Range(0, 4096).Select(index => Route($"10.{index / 256}.{index % 256}.1", 32)).ToArray();
        var result = new StrictRouteObservationResult(routes, true, true, true, ["Route observation exceeds the 4096-row limit."]);
        AssertIncompleteAndRejected(result, CompleteInterfaces());
    }

    [Theory]
    [InlineData("IPv4 interface properties could not be read.")]
    [InlineData("IPv6 interface properties could not be read.")]
    [InlineData("IPv4 and IPv6 interface properties could not be read.")]
    [InlineData("Interface identity or index is unavailable.")]
    public void InterfacePropertyOrIdentityFailureIsIncompleteAndRejects(string reason)
    {
        var interfaces = new StrictInterfaceObservationResult([EligibleInterface()], false, false, [reason]);
        AssertIncompleteAndRejected(CompleteRoutes(), interfaces);
    }

    [Fact]
    public void FailedOmittedInterfaceRejectsEvenIfVisibleEvidenceWouldAllowTarget()
    {
        var interfaces = new StrictInterfaceObservationResult([EligibleInterface()], false, false,
            ["Another interface could not be observed; collision status is unknown."]);
        AssertIncompleteAndRejected(CompleteRoutes(), interfaces);
    }

    [Theory]
    [InlineData("Interface local addresses exceed the 64-value limit.")]
    [InlineData("Interface gateways exceed the 64-value limit.")]
    [InlineData("Interface DNS servers exceed the 64-value limit.")]
    public void PerInterfaceValueLimitIsTruncatedIncompleteAndRejects(string reason)
    {
        var interfaces = new StrictInterfaceObservationResult([EligibleInterface()], false, true, [reason]);
        AssertIncompleteAndRejected(CompleteRoutes(), interfaces);
    }

    [Fact]
    public void InterfaceCountOverLimitIsTruncatedIncompleteAndRejects()
    {
        var visible = Enumerable.Range(1, 256).Select(EligibleInterface).ToArray();
        var interfaces = new StrictInterfaceObservationResult(visible, false, true,
            ["Interface observation exceeds the 256-interface limit."]);
        AssertIncompleteAndRejected(CompleteRoutes(), interfaces);
    }

    [Fact]
    public void CompleteBoundedObservationRemainsPositiveControl()
    {
        var inputs = ObservationAssembler.Create(CompleteRoutes(), CompleteInterfaces(), Options());
        Assert.True(inputs.ObservationComplete);
        Assert.True(RouteCandidatePolicy.Select(inputs).SafeCandidateExists);
    }

    [Fact]
    public void HarnessHasNoProjectOrAssemblyMutationDependency()
    {
        var assembly = typeof(RouteCandidatePolicy).Assembly;
        var libertyRouteReferences = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name).OfType<string>()
            .Where(name => name.StartsWith("LibertyRoute.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(["LibertyRoute.RouteObservation"], libertyRouteReferences);

        var toolNativeImports = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            .Select(method => (Method: method, Import: method.GetCustomAttributes(typeof(DllImportAttribute), false)
                .Cast<DllImportAttribute>().SingleOrDefault()))
            .Where(item => item.Import is not null)
            .Select(item => $"{item.Import!.Value}!{item.Method.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(toolNativeImports);

        var observationAssembly = typeof(ExactRouteVerifier).Assembly;
        var observationNativeImports = observationAssembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            .Select(method => (Method: method, Import: method.GetCustomAttributes(typeof(DllImportAttribute), false)
                .Cast<DllImportAttribute>().SingleOrDefault()))
            .Where(item => item.Import is not null)
            .Select(item => $"{item.Import!.Value}!{item.Method.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["iphlpapi.dll!FreeMibTable", "iphlpapi.dll!GetIpForwardTable2"], observationNativeImports);

        var project = File.ReadAllText(FindRepositoryFile("tools", "LibertyRoute.RouteObservation", "LibertyRoute.RouteObservation.csproj"));
        Assert.Contains("src\\LibertyRoute.RouteObservation\\LibertyRoute.RouteObservation.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Restoration", project, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Service", project, StringComparison.Ordinal);

        var source = File.ReadAllText(FindRepositoryFile("tools", "LibertyRoute.RouteObservation", "Program.cs"));
        foreach (var forbidden in ForbiddenMutationSymbols)
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetIpForwardTable2", source, StringComparison.Ordinal);
        Assert.Contains("FreeMibTable", source, StringComparison.Ordinal);
    }

    private static readonly string[] ForbiddenMutationSymbols =
    [
        "CreateIpForwardEntry2", "DeleteIpForwardEntry2", "SetIpForwardEntry", "RouteRestorationProvider",
        "WindowsRouteMutationNative", "IRestorationMutationProvider", "RecordedMutationExecutor",
        "MutationOwnershipCoordinator", "RecoveryExecutionCoordinator", "Process.Start", "New-NetRoute",
        "Remove-NetRoute", "Set-NetRoute"
    ];

    private static void AssertIncompleteAndRejected(
        StrictRouteObservationResult routes, StrictInterfaceObservationResult interfaces)
    {
        var inputs = ObservationAssembler.Create(routes, interfaces, Options());
        Assert.False(inputs.ObservationComplete);
        Assert.False(RouteCandidatePolicy.Select(inputs).SafeCandidateExists);
        Assert.NotEmpty(inputs.IncompleteReasons);
    }

    private static RouteObservationInputs Inputs(
        IReadOnlyList<CanonicalRouteObservation>? routes = null,
        InterfaceObservation? @interface = null,
        IReadOnlyList<InterfaceObservation>? interfaces = null,
        IReadOnlySet<int>? management = null,
        int? dedicatedInterfaceIndex = 42) =>
        ObservationAssembler.Create(
            new StrictRouteObservationResult(routes ?? [], true, true, false, []),
            new StrictInterfaceObservationResult(interfaces ?? [@interface ?? EligibleInterface()], true, false, []),
            new ObservationOptions(null, dedicatedInterfaceIndex, management ?? new HashSet<int>()));

    private static StrictRouteObservationResult CompleteRoutes() => new([], true, true, false, []);
    private static StrictInterfaceObservationResult CompleteInterfaces() => new([EligibleInterface()], true, false, []);
    private static ObservationOptions Options() => new(null, 42, new HashSet<int>());

    private static InterfaceObservation EligibleInterface(int index = 42) =>
        new(index, $"interface-{index}", "Dedicated test interface", NetworkInterfaceType.Ethernet.ToString(),
            OperationalStatus.Up.ToString(), [], [], [], false, false);

    private static CanonicalRouteObservation Route(string destination, int prefixLength, int interfaceIndex = 42) =>
        new(AddressFamily.InterNetwork.ToString(), destination, prefixLength, "0.0.0.0", interfaceIndex, 1);

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
