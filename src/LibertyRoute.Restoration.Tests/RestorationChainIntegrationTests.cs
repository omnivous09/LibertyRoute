using LibertyRoute.Core;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RestorationChainIntegrationTests
{
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task FullSyntheticChainRestoresMissingBaselineRoute()
    {
        var baseline = Snapshot(Route("10.0.0.0/24", "10.0.0.1", 4, 1));
        var current = Snapshot();
        var native = new FakeRouteMutationNative();
        var harness = Prepare(baseline, current, SessionId, native);

        Assert.True(harness.Preparation.CanExecuteAutomatically);
        var result = await ExecuteAllAsync(harness.Preparation, native);

        Assert.Single(result);
        Assert.Equal(RestorationMutationState.Succeeded, result[0].State);
        Assert.Single(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public async Task SuccessfulSyntheticRestorationBecomesIdempotent()
    {
        var harness = Prepare(Snapshot(Route("10.0.0.0/24", "10.0.0.1", 4, 1)), Snapshot(), SessionId, new FakeRouteMutationNative());
        var request = Assert.Single(harness.Preparation.AuthorizedRequests);
        var first = await harness.Provider.ApplyAsync(request, CancellationToken.None);
        var second = await harness.Provider.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(RestorationMutationState.Succeeded, first.State);
        Assert.Equal(RestorationMutationState.AlreadyRestored, second.State);
        Assert.Single(harness.Native.AddCalls);
    }

    [Fact]
    public void OwnedAddedRouteIsAuthorizedButBlockedBeforeProvider()
    {
        var applied = Route("10.0.0.0/24", "10.0.0.1", 4, 1);
        var native = new FakeRouteMutationNative { CurrentRoutes = new[] { applied } };
        var harness = Prepare(Snapshot(), Snapshot(applied), SessionId, native);

        Assert.Single(harness.DryRun.Operations);
        Assert.Equal(DryRunAction.ManualReview, harness.DryRun.Operations[0].Action);
        Assert.False(harness.Preparation.CanExecuteAutomatically);
        Assert.Empty(harness.Preparation.AuthorizedRequests);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public async Task ChangedRouteWithExternalCurrentStateIsDeniedByProvider()
    {
        var baseline = Route("10.0.0.0/24", "10.0.0.1", 4, 1);
        var applied = Route("10.0.0.0/24", "10.0.0.2", 4, 1);
        var native = new FakeRouteMutationNative { CurrentRoutes = new[] { applied } };
        var harness = Prepare(Snapshot(baseline), Snapshot(applied), SessionId, native);

        var request = Assert.Single(harness.Preparation.AuthorizedRequests);
        var result = await harness.Provider.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(RestorationMutationState.StateChangedExternally, result.State);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void OwnershipFailuresNeverReachProvider(bool includeEvidence, bool wrongOriginal, bool wrongApplied)
    {
        var baseline = Route("10.0.0.0/24", "10.0.0.1", 4, 1);
        var current = Snapshot();
        var plan = RestorationPlanner.CreatePlan(Snapshot(baseline), current);
        var dryRun = DryRunRestorationExecutor.CreateDryRun(plan);
        var operation = Assert.Single(dryRun.Operations);
        var evidence = includeEvidence
            ? new[] { Evidence(operation, wrongOriginal ? "wrong" : operation.OriginalValue, wrongApplied ? "wrong" : operation.CurrentValue) }
            : Array.Empty<OwnershipEvidence>();
        var native = new FakeRouteMutationNative();
        var preparation = Prepare(plan, dryRun, evidence, native);

        Assert.False(preparation.Preparation.CanExecuteAutomatically);
        Assert.Empty(preparation.Preparation.AuthorizedRequests);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public void WrongSessionEvidenceNeverProducesAnExecutionRequest()
    {
        var baseline = Route("10.0.0.0/24", "10.0.0.1", 4, 1);
        var plan = RestorationPlanner.CreatePlan(Snapshot(baseline), Snapshot());
        var dryRun = DryRunRestorationExecutor.CreateDryRun(plan);
        var operation = Assert.Single(dryRun.Operations);
        var native = new FakeRouteMutationNative();
        var preparation = Prepare(plan, dryRun, new[] { Evidence(operation, operation.OriginalValue, operation.CurrentValue, Guid.NewGuid()) }, native);

        Assert.False(preparation.Preparation.CanExecuteAutomatically);
        Assert.Empty(preparation.Preparation.AuthorizedRequests);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Theory]
    [InlineData(DryRunSafetyState.ManualReview)]
    [InlineData(DryRunSafetyState.Unsupported)]
    [InlineData(DryRunSafetyState.Unverifiable)]
    public void UnsafeOperationsNeverReachProvider(DryRunSafetyState state)
    {
        var operation = new DryRunRestorationOperation(
            DryRunOperationCategory.Route,
            state == DryRunSafetyState.ManualReview ? DryRunAction.ManualReview : DryRunAction.RestoreBaseline,
            "route-unsafe",
            state == DryRunSafetyState.ManualReview ? "<absent>" : "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1",
            "unsafe",
            1,
            true,
            false,
            state);
        var dryRun = new DryRunRestorationResult(new[] { operation }, new DryRunRestorationSummary(1, 1, 0, 1, 0, false, new[] { "blocked" }));
        var native = new FakeRouteMutationNative();
        var preparation = Prepare(dryRun, new[] { Evidence(operation, operation.OriginalValue, operation.CurrentValue) }, native);

        Assert.False(preparation.Preparation.CanExecuteAutomatically);
        Assert.Empty(preparation.Preparation.AuthorizedRequests);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public void BlockedBatchDoesNotPartiallyExecuteAuthorizedRoute()
    {
        var authorized = Operation("route-a", "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1", "<absent>", 1);
        var denied = Operation("route-b", "destination=192.0.2.0/24;nextHop=192.0.2.1;interfaceIndex=4;metric=1", "<absent>", 2);
        var dryRun = new DryRunRestorationResult(new[] { authorized, denied }, new DryRunRestorationSummary(2, 2, 2, 0, 0, false, Array.Empty<string>()));
        var native = new FakeRouteMutationNative();
        var preparation = Prepare(dryRun, new[] { Evidence(authorized, authorized.OriginalValue, authorized.CurrentValue) }, native);

        Assert.False(preparation.Preparation.CanExecuteAutomatically);
        Assert.Single(preparation.Preparation.AuthorizedRequests);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public async Task ThreeRouteOperationsExecuteInDeterministicPreparationOrder()
    {
        var routes = new[]
        {
            Route("192.0.2.0/24", "192.0.2.1", 4, 1),
            Route("10.0.0.0/8", "10.0.0.1", 4, 1),
            Route("2001:db8::/32", "2001:db8::1", 6, 1, "23")
        };
        var native = new FakeRouteMutationNative();
        var harness = Prepare(Snapshot(routes), Snapshot(), SessionId, native);
        var results = await ExecuteAllAsync(harness.Preparation, native);

        Assert.True(harness.Preparation.CanExecuteAutomatically);
        Assert.Equal(new[] { "2001:db8::/32", "10.0.0.0/8", "192.0.2.0/24" }, native.AddCalls.Select(call => call.Destination));
        Assert.Equal(new[] { RestorationMutationState.Succeeded, RestorationMutationState.Succeeded, RestorationMutationState.Succeeded }, results.Select(result => result.State));
    }

    [Fact]
    public async Task FakeFailureStopsLaterOperationsAndKeepsEarlierResult()
    {
        var routes = new[]
        {
            Route("10.0.0.0/8", "10.0.0.1", 4, 1),
            Route("192.0.2.0/24", "192.0.2.1", 4, 1),
            Route("2001:db8::/32", "2001:db8::1", 6, 1, "23")
        };
        var native = new FakeRouteMutationNative { FailAddAtCall = 2 };
        var harness = Prepare(Snapshot(routes), Snapshot(), SessionId, native);
        var results = await ExecuteFailStopAsync(harness.Preparation, native);

        Assert.Equal(2, results.Count);
        Assert.Equal(RestorationMutationState.Succeeded, results[0].State);
        Assert.Equal(RestorationMutationState.Failed, results[1].State);
        Assert.Equal(2, native.AddCalls.Count);
    }

    [Fact]
    public async Task CancellationBeforeProviderInvocationMakesZeroMutationCalls()
    {
        var native = new FakeRouteMutationNative();
        var harness = Prepare(Snapshot(Route("10.0.0.0/24", "10.0.0.1", 4, 1)), Snapshot(), SessionId, native);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteAllAsync(harness.Preparation, native, source.Token));
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public void CancellationBeforeExecutionPreparationKeepsBatchUnexecuted()
    {
        var native = new FakeRouteMutationNative();
        var harness = Prepare(Snapshot(Route("10.0.0.0/24", "10.0.0.1", 4, 1)), Snapshot(), SessionId, native);
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.True(harness.Preparation.CanExecuteAutomatically);
        Assert.True(source.IsCancellationRequested);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.DeleteCalls);
    }

    [Fact]
    public void IntegrationHarnessDoesNotConstructRealWindowsAdapter()
    {
        Assert.DoesNotContain(typeof(RestorationChainIntegrationTests).Assembly.GetTypes(), type => type == typeof(WindowsRouteMutationNative));
        Assert.DoesNotContain(typeof(RestorationChainIntegrationTests).Assembly.GetTypes(), type => type == typeof(WindowsRouteNativeApi));
    }

    private static ChainHarness Prepare(NetworkStateSnapshot baseline, NetworkStateSnapshot current, Guid sessionId, FakeRouteMutationNative native)
    {
        var plan = RestorationPlanner.CreatePlan(baseline, current);
        var dryRun = DryRunRestorationExecutor.CreateDryRun(plan);
        var evidence = dryRun.Operations.Select(operation => Evidence(operation, operation.OriginalValue, operation.CurrentValue)).ToArray();
        return Prepare(plan, dryRun, evidence, native, sessionId);
    }

    private static ChainHarness Prepare(DryRunRestorationResult dryRun, IReadOnlyList<OwnershipEvidence> evidence, FakeRouteMutationNative native)
        => Prepare(new RestorationPlan(Array.Empty<RestorationDifference>()), dryRun, evidence, native, SessionId);

    private static ChainHarness Prepare(RestorationPlan plan, DryRunRestorationResult dryRun, IReadOnlyList<OwnershipEvidence> evidence, FakeRouteMutationNative native, Guid sessionId = default)
    {
        var activeSession = sessionId == Guid.Empty ? SessionId : sessionId;
        var authorization = RestorationAuthorizationPolicy.AuthorizeBatch(dryRun, evidence, activeSession);
        var preparation = RestorationExecutionPreparation.Prepare(authorization, TransactionId, activeSession);
        return new ChainHarness(plan, dryRun, authorization, preparation, native, new RouteRestorationProvider(native));
    }

    private static async Task<IReadOnlyList<RestorationMutationResult>> ExecuteAllAsync(RestorationExecutionPreparation preparation, FakeRouteMutationNative native, CancellationToken cancellationToken = default)
    {
        if (!preparation.CanExecuteAutomatically)
            return Array.Empty<RestorationMutationResult>();

        var results = new List<RestorationMutationResult>();
        foreach (var request in preparation.AuthorizedRequests)
            results.Add(await new RouteRestorationProvider(native).ApplyAsync(request, cancellationToken));
        return results;
    }

    private static async Task<IReadOnlyList<RestorationMutationResult>> ExecuteFailStopAsync(RestorationExecutionPreparation preparation, FakeRouteMutationNative native)
    {
        var results = new List<RestorationMutationResult>();
        foreach (var request in preparation.AuthorizedRequests)
        {
            var result = await new RouteRestorationProvider(native).ApplyAsync(request, CancellationToken.None);
            results.Add(result);
            if (result.State != RestorationMutationState.Succeeded)
                break;
        }

        return results;
    }

    private static OwnershipEvidence Evidence(DryRunRestorationOperation operation, string original, string applied, Guid? session = null)
        => new(session ?? SessionId, operation.Category, operation.TargetIdentity, original, applied, Guid.NewGuid(), DateTimeOffset.UnixEpoch, operation.ExecutionOrder, OwnershipEvidenceSource.TestFixture, true);

    private static DryRunRestorationOperation Operation(string target, string original, string current, int order)
        => new(DryRunOperationCategory.Route, DryRunAction.RestoreBaseline, target, original, current, "synthetic", order, true, true, DryRunSafetyState.SafeToPlan);

    private static NetworkStateSnapshot Snapshot(params RouteState[] routes)
        => new(DateTimeOffset.UnixEpoch, "synthetic", Array.Empty<AdapterState>(), routes, null);

    private static RouteState Route(string destination, string nextHop, int interfaceIndex, int metric, string family = "2")
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = interfaceIndex, Metric = (uint)metric, AddressFamily = family };

    private sealed record ChainHarness(
        RestorationPlan Plan,
        DryRunRestorationResult DryRun,
        BatchAuthorizationResult Authorization,
        RestorationExecutionPreparation Preparation,
        FakeRouteMutationNative Native,
        RouteRestorationProvider Provider);

    private sealed class FakeRouteMutationNative : IRouteMutationNative
    {
        public IReadOnlyList<RouteState> CurrentRoutes { get; set; } = Array.Empty<RouteState>();
        public int FailAddAtCall { get; set; }
        public List<RouteRestorationCommand> AddCalls { get; } = new();
        public List<RouteRestorationCommand> DeleteCalls { get; } = new();

        public Task<RouteQueryResult> QueryAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var route = CurrentRoutes.FirstOrDefault(candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.Destination, command.Destination));
            return Task.FromResult(new RouteQueryResult(route is not null, route));
        }

        public Task<bool> AddRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCalls.Add(command);
            if (FailAddAtCall == AddCalls.Count)
                return Task.FromResult(false);

            CurrentRoutes = CurrentRoutes.Concat(new[]
            {
                new RouteState { Destination = command.Destination, NextHop = command.NextHop, InterfaceIndex = command.InterfaceIndex, Metric = command.Metric, AddressFamily = ((int)command.AddressFamily).ToString() }
            }).ToArray();
            return Task.FromResult(true);
        }

        public Task<bool> DeleteRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls.Add(command);
            CurrentRoutes = CurrentRoutes.Where(route => !StringComparer.OrdinalIgnoreCase.Equals(route.Destination, command.Destination)).ToArray();
            return Task.FromResult(true);
        }
    }
}
