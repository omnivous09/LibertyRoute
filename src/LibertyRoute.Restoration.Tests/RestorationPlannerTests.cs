using LibertyRoute.Core;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Tests;

public sealed class RestorationPlannerTests
{
    [Fact]
    public void IdenticalSnapshotsProduceEmptyPlan()
    {
        var snapshot = Snapshot();
        Assert.Empty(RestorationPlanner.CreatePlan(snapshot, snapshot).Differences);
    }

    [Fact]
    public void MissingAdapterIsRestorationCandidate()
    {
        var original = Snapshot(adapter: Adapter("a", addresses: new[] { "192.0.2.1" }));
        var current = Snapshot();
        var difference = Single(original, current, RestorationCategory.Adapter);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedAdapterIsNeverAutomaticallyDeleted()
    {
        var difference = Single(Snapshot(), Snapshot(adapter: Adapter("a")), RestorationCategory.Adapter);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Theory]
    [InlineData("192.0.2.1", RestorationCategory.Address)]
    [InlineData("2001:db8::1", RestorationCategory.Address)]
    public void RemovedAddressIsRestorationCandidate(string address, RestorationCategory category)
    {
        var original = Snapshot(adapter: Adapter("a", addresses: new[] { address }));
        var difference = Single(original, Snapshot(adapter: Adapter("a")), category);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedAddressIsHandledConservatively()
    {
        var difference = Single(Snapshot(adapter: Adapter("a")), Snapshot(adapter: Adapter("a", addresses: new[] { "192.0.2.2" })), RestorationCategory.Address);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Fact]
    public void ChangedGatewayIsReported()
    {
        var original = Snapshot(adapter: Adapter("a", gateways: new[] { "192.0.2.1" }));
        var current = Snapshot(adapter: Adapter("a", gateways: new[] { "192.0.2.254" }));
        var difference = Single(original, current, RestorationCategory.Gateway);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal("192.0.2.1", difference.OriginalValue);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void ChangedDnsServerIsReported()
    {
        var original = Snapshot(adapter: Adapter("a", dns: new[] { "192.0.2.53" }));
        var current = Snapshot(adapter: Adapter("a", dns: new[] { "192.0.2.54" }));
        var difference = Single(original, current, RestorationCategory.Dns);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void RemovedRouteIsRestorationCandidate()
    {
        var route = Route("10.0.0.0/8");
        var difference = Single(Snapshot(routes: new[] { route }), Snapshot(), RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Missing, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
    }

    [Fact]
    public void AddedRouteIsNeverAutomaticallyDeleted()
    {
        var difference = Single(Snapshot(), Snapshot(routes: new[] { Route("10.0.0.0/8") }), RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Added, difference.Classification);
        Assert.Equal(RestorationIntent.NoAutomaticDeletion, difference.Intent);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.2", 4, 1, 4, "next hop")]
    [InlineData("10.0.0.1", "10.0.0.1", 4, 2, 4, "metric")]
    [InlineData("10.0.0.1", "10.0.0.1", 4, 1, 8, "interface")]
    public void ChangedRouteFieldIsReported(string originalNextHop, string currentNextHop, int originalMetric, int currentMetric, int currentInterface, string reason)
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", originalNextHop, 4, originalMetric) });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", currentNextHop, currentInterface, currentMetric) });
        var difference = Single(original, current, RestorationCategory.Route);

        Assert.Equal(DifferenceClassification.Changed, difference.Classification);
        Assert.Equal(RestorationIntent.RestoreBaseline, difference.Intent);
        Assert.Contains(reason, difference.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IPv4AndIPv6RoutesRemainDistinct()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", family: "2"), Route("2001:db8::/32", family: "23") });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", family: "23"), Route("2001:db8::/32", family: "2") });

        Assert.Equal(4, RestorationPlanner.CreatePlan(original, current).Differences.Count);
    }

    [Fact]
    public void RouteIdentityDoesNotDependOnColonOrSlashFormatting()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8") });
        var current = Snapshot(routes: new[] { Route("10.0.0.0:8") });

        Assert.Empty(RestorationPlanner.CreatePlan(original, current).Differences);
    }

    [Fact]
    public void PlanOrderingIsDeterministic()
    {
        var original = Snapshot(
            adapter: Adapter("b", addresses: new[] { "192.0.2.2" }),
            routes: new[] { Route("10.0.0.0/8") });
        var current = Snapshot(adapter: Adapter("a"));

        var first = RestorationPlanner.CreatePlan(original, current).Differences;
        var second = RestorationPlanner.CreatePlan(original, current).Differences;

        Assert.Equal(first, second);
    }

    [Fact]
    public void NullCollectionsAreHandledAsEmpty()
    {
        var original = new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test", null!, null, null);
        var current = new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), null, null);

        Assert.Empty(RestorationPlanner.CreatePlan(original, current).Differences);
    }

    [Fact]
    public void PlannerExposesNoMutationSurface()
    {
        var publicMethods = typeof(RestorationPlanner).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(RestorationPlanner.CreatePlan) }, publicMethods);
        Assert.Empty(typeof(RestorationPlanner).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
            .SelectMany(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), inherit: false)));
    }

    private static RestorationDifference Single(NetworkStateSnapshot original, NetworkStateSnapshot current, RestorationCategory category)
        => Assert.Single(RestorationPlanner.CreatePlan(original, current).Differences, difference => difference.Category == category);

    private static NetworkStateSnapshot Snapshot(AdapterState? adapter = null, IReadOnlyList<RouteState>? routes = null)
        => new(DateTimeOffset.UnixEpoch, "test", adapter is null ? Array.Empty<AdapterState>() : new[] { adapter }, routes, null);

    private static AdapterState Adapter(string id, IReadOnlyList<string>? addresses = null, IReadOnlyList<string>? gateways = null, IReadOnlyList<string>? dns = null)
        => new(id, id, "test", "Ethernet", "Up", addresses ?? Array.Empty<string>(), gateways ?? Array.Empty<string>(), dns ?? Array.Empty<string>());

    private static RouteState Route(string destination, string nextHop = "0.0.0.0", int interfaceIndex = 4, int metric = 1, string family = "2")
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = interfaceIndex, Metric = (uint)metric, AddressFamily = family };
}
