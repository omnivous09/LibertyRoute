using LibertyRoute.Core;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RouteRestorationProviderTests
{
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task NonRouteRequestIsUnsupportedWithoutNativeCalls()
    {
        var native = new FakeRouteMutationNative();
        var result = await ApplyAsync(native, Operation(DryRunOperationCategory.Address, "adapter-a", "192.0.2.1", "<absent>"));

        Assert.Equal(RestorationMutationState.Unsupported, result.State);
        Assert.Empty(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Theory]
    [InlineData("destination=not-an-ip/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1")]
    [InlineData("destination=10.0.0.0/nope;nextHop=10.0.0.1;interfaceIndex=4;metric=1")]
    [InlineData("destination=10.0.0.0/24;nextHop=not-an-ip;interfaceIndex=4;metric=1")]
    [InlineData("destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=0;metric=1")]
    [InlineData("destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=-1;metric=1")]
    public async Task MalformedRouteDataFailsWithoutNativeCalls(string routeValue)
    {
        var native = new FakeRouteMutationNative();
        var result = await ApplyAsync(native, Operation(DryRunOperationCategory.Route, "route-a", routeValue, "<absent>"));

        Assert.Equal(RestorationMutationState.Failed, result.State);
        Assert.Empty(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public async Task IPv4AndIPv6MismatchFailsWithoutNativeCalls()
    {
        var native = new FakeRouteMutationNative();
        var result = await ApplyAsync(native, Operation(
            DryRunOperationCategory.Route,
            "route-a",
            "destination=10.0.0.0/24;nextHop=fe80::1;interfaceIndex=4;metric=1",
            "<absent>"));

        Assert.Equal(RestorationMutationState.Failed, result.State);
        Assert.Empty(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public async Task CancellationBeforeMutationMakesZeroCalls()
    {
        var native = new FakeRouteMutationNative();
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ApplyAsync(native, RouteOperation(), source.Token));

        Assert.Empty(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public async Task MissingBaselineRouteAlreadyPresentIsAlreadyRestored()
    {
        var native = new FakeRouteMutationNative
        {
            CurrentRoute = new RouteState
            {
                Destination = "10.0.0.0/24",
                NextHop = "10.0.0.1",
                InterfaceIndex = 4,
                Metric = 1,
                AddressFamily = "2"
            }
        };

        var result = await ApplyAsync(native, RouteOperation());

        Assert.Equal(RestorationMutationState.AlreadyRestored, result.State);
        Assert.Single(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public async Task ConflictingCurrentRouteIsStateChangedExternally()
    {
        var native = new FakeRouteMutationNative
        {
            CurrentRoute = new RouteState
            {
                Destination = "10.0.0.0/24",
                NextHop = "10.0.0.2",
                InterfaceIndex = 4,
                Metric = 1,
                AddressFamily = "2"
            }
        };

        var result = await ApplyAsync(native, RouteOperation());

        Assert.Equal(RestorationMutationState.StateChangedExternally, result.State);
        Assert.Single(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public async Task EligibleBaselineRouteRestoreCallsFakeAddExactlyOnce()
    {
        var native = new FakeRouteMutationNative();
        var result = await ApplyAsync(native, RouteOperation());

        Assert.Equal(RestorationMutationState.Succeeded, result.State);
        Assert.Single(native.QueryCalls);
        Assert.Single(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
        Assert.Equal("10.0.0.0/24", native.AddCalls[0].Destination);
    }

    [Fact]
    public async Task EligibleOwnedRouteRemovalCallsFakeRemoveExactlyOnce()
    {
        var native = new FakeRouteMutationNative();
        var result = await ApplyAsync(native, Operation(
            DryRunOperationCategory.Route,
            "route-a",
            "<absent>",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1"));

        Assert.Equal(RestorationMutationState.Succeeded, result.State);
        Assert.Single(native.QueryCalls);
        Assert.Empty(native.AddCalls);
        Assert.Single(native.RemoveCalls);
    }

    [Theory]
    [InlineData(true, RestorationMutationState.Succeeded)]
    [InlineData(false, RestorationMutationState.Failed)]
    public async Task FakeAddOutcomeIsReported(bool succeeds, RestorationMutationState expectedState)
    {
        var native = new FakeRouteMutationNative { AddSucceeds = succeeds };
        var result = await ApplyAsync(native, RouteOperation());

        Assert.Equal(expectedState, result.State);
        Assert.Single(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Theory]
    [InlineData(true, RestorationMutationState.Succeeded)]
    [InlineData(false, RestorationMutationState.Failed)]
    public async Task FakeRemoveOutcomeIsReported(bool succeeds, RestorationMutationState expectedState)
    {
        var native = new FakeRouteMutationNative { RemoveSucceeds = succeeds };
        var result = await ApplyAsync(native, Operation(
            DryRunOperationCategory.Route,
            "route-a",
            "<absent>",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1"));

        Assert.Equal(expectedState, result.State);
        Assert.Empty(native.AddCalls);
        Assert.Single(native.RemoveCalls);
    }

    [Fact]
    public async Task RepeatedInvocationBecomesAlreadyRestoredAfterFakeStateChanges()
    {
        var native = new FakeRouteMutationNative();
        var request = Request(RouteOperation());

        var first = await new RouteRestorationProvider(native).ApplyAsync(request, CancellationToken.None);
        var second = await new RouteRestorationProvider(native).ApplyAsync(request, CancellationToken.None);

        Assert.Equal(RestorationMutationState.Succeeded, first.State);
        Assert.Equal(RestorationMutationState.AlreadyRestored, second.State);
        Assert.Single(native.AddCalls);
        Assert.Equal(2, native.QueryCalls.Count);
    }

    [Fact]
    public async Task ProviderDoesNotMutateWhenCurrentStateDiffers()
    {
        var native = new FakeRouteMutationNative
        {
            CurrentRoute = new RouteState
            {
                Destination = "10.0.0.0/24",
                NextHop = "10.0.0.1",
                InterfaceIndex = 9,
                Metric = 1,
                AddressFamily = "2"
            }
        };

        var result = await ApplyAsync(native, RouteOperation());

        Assert.Equal(RestorationMutationState.StateChangedExternally, result.State);
        Assert.Empty(native.AddCalls);
        Assert.Empty(native.RemoveCalls);
    }

    [Fact]
    public void CommandTranslationIsDeterministic()
    {
        var request = Request(RouteOperation());
        var first = RouteRestorationCommand.FromRequest(request);
        var second = RouteRestorationCommand.FromRequest(request);

        Assert.Equal(first, second);
        Assert.Equal(RouteAddressFamily.IPv4, first.AddressFamily);
        Assert.Equal("10.0.0.0/24", first.Destination);
        Assert.Equal("10.0.0.1", first.NextHop);
        Assert.Equal(4, first.InterfaceIndex);
        Assert.Equal((uint)1, first.Metric);
        Assert.Equal(RouteMutationAction.Add, first.Action);
    }

    [Theory]
    [InlineData("10.0.0.0/24", "10.0.0.0/24")]
    [InlineData("10.0.0.0:24", "10.0.0.0/24")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    public void PrefixNotationIsNormalized(string input, string expectedDestination)
    {
        Assert.True(RouteRestorationCommand.TryParseRouteValue(
            $"destination={input};nextHop={(input.Contains(':') && !input.Contains('.') ? "2001:db8::1" : "10.0.0.1")};interfaceIndex=4;metric=1",
            out var command,
            out var reason), reason);

        Assert.Equal(expectedDestination, command!.Destination);
    }

    [Fact]
    public void IPv4DefaultRouteParses()
    {
        var command = Parse("destination=0.0.0.0/0;nextHop=0.0.0.0;interfaceIndex=4;metric=1");

        Assert.Equal(RouteAddressFamily.IPv4, command.AddressFamily);
        Assert.Equal("0.0.0.0/0", command.Destination);
    }

    [Fact]
    public void IPv6DefaultRouteParses()
    {
        var command = Parse("destination=::/0;nextHop=::;interfaceIndex=4;metric=1");

        Assert.Equal(RouteAddressFamily.IPv6, command.AddressFamily);
        Assert.Equal("::/0", command.Destination);
    }

    [Theory]
    [InlineData("fe80::/64", "fe80::1")]
    [InlineData("ff02::/16", "::")]
    public void IPv6LinkLocalAndMulticastRoutesParse(string destination, string nextHop)
    {
        var command = Parse($"destination={destination};nextHop={nextHop};interfaceIndex=4;metric=1");

        Assert.Equal(RouteAddressFamily.IPv6, command.AddressFamily);
        Assert.Equal(destination, command.Destination);
    }

    [Fact]
    public void ProviderAcceptsAuthorizedRestorationRequestOnly()
    {
        var method = typeof(IRestorationMutationProvider).GetMethod(nameof(IRestorationMutationProvider.ApplyAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(AuthorizedRestorationRequest), method!.GetParameters()[0].ParameterType);
    }

    private static async Task<RestorationMutationResult> ApplyAsync(FakeRouteMutationNative native, DryRunRestorationOperation operation, CancellationToken cancellationToken = default)
        => await new RouteRestorationProvider(native).ApplyAsync(Request(operation), cancellationToken);

    private static AuthorizedRestorationRequest Request(DryRunRestorationOperation operation)
    {
        var evidence = new OwnershipEvidence(
            SessionId,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            DateTimeOffset.UnixEpoch,
            1,
            OwnershipEvidenceSource.TestFixture,
            true);
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);
        return AuthorizedRestorationRequest.Create(operation, authorization, TransactionId, SessionId);
    }

    private static DryRunRestorationOperation RouteOperation()
        => Operation(
            DryRunOperationCategory.Route,
            "route-a",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1",
            "<absent>");

    private static DryRunRestorationOperation Operation(DryRunOperationCategory category, string target, string original, string current)
        => new(
            category,
            DryRunAction.RestoreBaseline,
            target,
            original,
            current,
            "test",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

    private static RouteRestorationCommand Parse(string value)
    {
        Assert.True(RouteRestorationCommand.TryParseRouteValue(value, out var command, out var reason), reason);
        return command!;
    }

    private sealed class FakeRouteMutationNative : IRouteMutationNative
    {
        public RouteState? CurrentRoute { get; set; }
        public bool AddSucceeds { get; set; } = true;
        public bool RemoveSucceeds { get; set; } = true;
        public List<RouteRestorationCommand> QueryCalls { get; } = new();
        public List<RouteRestorationCommand> AddCalls { get; } = new();
        public List<RouteRestorationCommand> RemoveCalls { get; } = new();

        public Task<RouteQueryResult> QueryAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryCalls.Add(command);
            return Task.FromResult(new RouteQueryResult(CurrentRoute is not null, CurrentRoute));
        }

        public Task<bool> AddRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCalls.Add(command);
            if (AddSucceeds)
            {
                CurrentRoute = new RouteState
                {
                    Destination = command.Destination,
                    NextHop = command.NextHop,
                    InterfaceIndex = command.InterfaceIndex,
                    Metric = command.Metric,
                    AddressFamily = ((int)command.AddressFamily).ToString()
                };
            }

            return Task.FromResult(AddSucceeds);
        }

        public Task<bool> DeleteRouteAsync(RouteRestorationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCalls.Add(command);
            if (RemoveSucceeds)
                CurrentRoute = null;

            return Task.FromResult(RemoveSucceeds);
        }
    }
}
