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

    [Fact]
    public void IdenticalPlanProducesZeroOperations()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(new RestorationPlan(Array.Empty<RestorationDifference>()));

        Assert.Empty(result.Operations);
        Assert.Equal(0, result.Summary.TotalDifferences);
    }

    [Fact]
    public void MissingBaselineAddressProducesNonAutomaticRestoration()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" })), Snapshot(Adapter("a")));
        var operation = SingleOperation(plan, DryRunOperationCategory.Address);

        Assert.Equal(DryRunAction.RestoreBaseline, operation.Action);
        Assert.Equal(DryRunSafetyState.SafeToPlan, operation.SafetyState);
        Assert.False(operation.AutomaticExecutionAllowed);
        Assert.True(operation.OwnershipRequired);
    }

    [Fact]
    public void AddedAddressProducesManualReviewAndNoDeletion()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" })));
        var operation = SingleOperation(plan, DryRunOperationCategory.Address);

        Assert.Equal(DryRunAction.ManualReview, operation.Action);
        Assert.Equal(DryRunSafetyState.ManualReview, operation.SafetyState);
        Assert.Contains("not automatically deleted", operation.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBaselineRouteProducesRestoration()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(routes: new[] { Route("10.0.0.0/8") }), Snapshot()));

        Assert.Equal(DryRunAction.RestoreBaseline, Assert.Single(result.Operations).Action);
    }

    [Fact]
    public void AddedBaselineRouteProducesNoDeletion()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(), Snapshot(routes: new[] { Route("10.0.0.0/8") })));

        Assert.Equal(DryRunAction.ManualReview, Assert.Single(result.Operations).Action);
    }

    [Fact]
    public void ChangedGatewayAndDnsProduceRestorationOperations()
    {
        var original = Snapshot(Adapter("a", gateways: new[] { "192.0.2.1" }, dns: new[] { "192.0.2.53" }));
        var current = Snapshot(Adapter("a", gateways: new[] { "192.0.2.254" }, dns: new[] { "192.0.2.54" }));
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        Assert.Contains(result.Operations, operation => operation.Category == DryRunOperationCategory.Gateway && operation.Action == DryRunAction.RestoreBaseline);
        Assert.Contains(result.Operations, operation => operation.Category == DryRunOperationCategory.Dns && operation.Action == DryRunAction.RestoreBaseline);
    }

    [Fact]
    public void ChangedRouteFieldsProduceRestorationOperations()
    {
        var original = Snapshot(routes: new[] { Route("10.0.0.0/8", "10.0.0.1", 4, 1) });
        var current = Snapshot(routes: new[] { Route("10.0.0.0/8", "10.0.0.2", 8, 2) });
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        var operation = Assert.Single(result.Operations);
        Assert.Equal(DryRunOperationCategory.Route, operation.Category);
        Assert.Equal(DryRunAction.RestoreBaseline, operation.Action);
        Assert.Equal(DryRunSafetyState.SafeToPlan, operation.SafetyState);
    }

    [Fact]
    public void MissingAdapterIsUnsupportedAndNeverAutomatic()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot()));
        var operation = SingleOperation(result, DryRunOperationCategory.Adapter);

        Assert.Equal(DryRunSafetyState.Unsupported, operation.SafetyState);
        Assert.False(operation.AutomaticExecutionAllowed);
    }

    [Fact]
    public void ExecutionOrderingAndNumberingAreStable()
    {
        var original = Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" }, gateways: new[] { "192.0.2.1" }, dns: new[] { "192.0.2.53" }), new[] { Route("10.0.0.0/8") });
        var current = Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" }, gateways: new[] { "192.0.2.254" }, dns: new[] { "192.0.2.54" }), new[] { Route("10.0.0.0/8", "10.0.0.1", 8, 2) });
        var first = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));
        var second = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(original, current));

        Assert.Equal(first.Operations, second.Operations);
        Assert.Equal(first.Summary.TotalDifferences, second.Summary.TotalDifferences);
        Assert.Equal(first.Summary.TotalOperations, second.Summary.TotalOperations);
        Assert.Equal(first.Summary.SafeOperations, second.Summary.SafeOperations);
        Assert.Equal(first.Summary.ManualReviewOperations, second.Summary.ManualReviewOperations);
        Assert.Equal(first.Summary.UnsupportedOperations, second.Summary.UnsupportedOperations);
        Assert.Equal(first.Summary.IsFullyExecutableInFuture, second.Summary.IsFullyExecutableInFuture);
        Assert.Equal(first.Summary.BlockingReasons, second.Summary.BlockingReasons);
        Assert.Equal(Enumerable.Range(1, first.Operations.Count), first.Operations.Select(operation => operation.ExecutionOrder));
        Assert.Equal(first.Operations.OrderBy(operation => operation.Category switch
        {
            DryRunOperationCategory.Route => 30,
            DryRunOperationCategory.Address => 40,
            DryRunOperationCategory.Gateway => 50,
            DryRunOperationCategory.Dns => 60,
            _ => 80
        }).ToArray(), first.Operations);
    }

    [Fact]
    public void SummaryReportsSafetyBlockers()
    {
        var result = DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" }))));

        Assert.Equal(1, result.Summary.TotalDifferences);
        Assert.Equal(1, result.Summary.TotalOperations);
        Assert.Equal(1, result.Summary.ManualReviewOperations);
        Assert.False(result.Summary.IsFullyExecutableInFuture);
        Assert.NotEmpty(result.Summary.BlockingReasons);
    }

    [Fact]
    public void DryRunDoesNotAlterInputPlan()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a")), Snapshot(Adapter("a", addresses: new[] { "192.0.2.2" })));
        var before = plan.Differences.ToArray();

        _ = DryRunRestorationExecutor.CreateDryRun(plan);

        Assert.Equal(before, plan.Differences);
    }

    [Fact]
    public void RestorationAssemblyHasNoWindowsOrMutationSurface()
    {
        var assembly = typeof(DryRunRestorationExecutor).Assembly;
        Assert.DoesNotContain(assembly.GetTypes(), type => type.Namespace?.StartsWith("System.Net.NetworkInformation", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("Microsoft.Win32", StringComparison.Ordinal) == true);
        Assert.Empty(assembly.GetTypes().SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)).SelectMany(method => method.GetCustomAttributes(typeof(System.Runtime.InteropServices.DllImportAttribute), false)));
        Assert.DoesNotContain(typeof(DryRunRestorationExecutor).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static), method => method.Name.Contains("Apply", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("ExecuteMutation", StringComparison.OrdinalIgnoreCase) || method.Name.Contains("Commit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepeatedDryRunExecutionIsEquivalent()
    {
        var plan = RestorationPlanner.CreatePlan(Snapshot(Adapter("a", addresses: new[] { "192.0.2.1" })), Snapshot(Adapter("a")));

        var first = DryRunRestorationExecutor.CreateDryRun(plan);
        var second = DryRunRestorationExecutor.CreateDryRun(plan);

        Assert.Equal(first.Operations, second.Operations);
        Assert.Equal(first.Summary.TotalDifferences, second.Summary.TotalDifferences);
        Assert.Equal(first.Summary.TotalOperations, second.Summary.TotalOperations);
        Assert.Equal(first.Summary.SafeOperations, second.Summary.SafeOperations);
        Assert.Equal(first.Summary.ManualReviewOperations, second.Summary.ManualReviewOperations);
        Assert.Equal(first.Summary.UnsupportedOperations, second.Summary.UnsupportedOperations);
        Assert.Equal(first.Summary.IsFullyExecutableInFuture, second.Summary.IsFullyExecutableInFuture);
        Assert.True(first.Summary.BlockingReasons.SequenceEqual(second.Summary.BlockingReasons));
    }

    private static RestorationDifference Single(NetworkStateSnapshot original, NetworkStateSnapshot current, RestorationCategory category)
        => Assert.Single(RestorationPlanner.CreatePlan(original, current).Differences, difference => difference.Category == category);

    private static DryRunRestorationOperation SingleOperation(RestorationPlan plan, DryRunOperationCategory category)
        => Assert.Single(DryRunRestorationExecutor.CreateDryRun(plan).Operations, operation => operation.Category == category);

    private static DryRunRestorationOperation SingleOperation(DryRunRestorationResult result, DryRunOperationCategory category)
        => Assert.Single(result.Operations, operation => operation.Category == category);

    private static NetworkStateSnapshot Snapshot(AdapterState? adapter = null, IReadOnlyList<RouteState>? routes = null)
        => new(DateTimeOffset.UnixEpoch, "test", adapter is null ? Array.Empty<AdapterState>() : new[] { adapter }, routes, null);

    private static AdapterState Adapter(string id, IReadOnlyList<string>? addresses = null, IReadOnlyList<string>? gateways = null, IReadOnlyList<string>? dns = null)
        => new(id, id, "test", "Ethernet", "Up", addresses ?? Array.Empty<string>(), gateways ?? Array.Empty<string>(), dns ?? Array.Empty<string>());

    private static RouteState Route(string destination, string nextHop = "0.0.0.0", int interfaceIndex = 4, int metric = 1, string family = "2")
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = interfaceIndex, Metric = (uint)metric, AddressFamily = family };
}
