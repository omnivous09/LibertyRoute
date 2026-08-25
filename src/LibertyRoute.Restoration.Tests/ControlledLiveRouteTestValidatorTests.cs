using LibertyRoute.Core;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlledLiveRouteTestValidatorTests
{
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("192.0.2.0/24")]
    [InlineData("198.51.100.0/24")]
    [InlineData("203.0.113.0/24")]
    public void AbsentTestNetPrefixCanBeEligible(string destination)
    {
        var result = Validate(destination: destination);

        Assert.True(result.IsEligible);
        Assert.Equal(LiveRouteTestEligibilityStatus.Eligible, result.Status);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.16.0.0/21")]
    [InlineData("10.16.0.1/32")]
    public void DefaultAndLocalDestinationsAreRejected(string destination)
    {
        var result = Validate(destination: destination, nextHop: destination.Contains(':') ? "::" : "0.0.0.0", localPrefixes: new[] { "10.16.0.0/21" });

        Assert.False(result.IsEligible);
        Assert.Equal(LiveRouteTestEligibilityStatus.UnsafeDestination, result.Status);
    }

    [Fact]
    public void ExistingTestNetRouteIsRejected()
    {
        var result = Validate(currentRoutes: new[] { Route("192.0.2.0/24") });

        Assert.Equal(LiveRouteTestEligibilityStatus.ExistingRoute, result.Status);
    }

    [Fact]
    public void GatewayAndDnsNextHopsAreRejected()
    {
        var gateway = Validate(nextHop: "192.0.2.1", gatewayAddresses: new[] { "192.0.2.1" });
        var dns = Validate(nextHop: "192.0.2.2", dnsAddresses: new[] { "192.0.2.2" });

        Assert.Equal(LiveRouteTestEligibilityStatus.UnsafeNextHop, gateway.Status);
        Assert.Equal(LiveRouteTestEligibilityStatus.UnsafeNextHop, dns.Status);
    }

    [Fact]
    public void UnknownAndDefaultRouteInterfacesAreRejected()
    {
        var unknown = Validate(interfaceIndex: 99, knownInterfaces: new HashSet<int>());
        var defaultInterface = Validate(interfaceIndex: 20, knownInterfaces: new HashSet<int> { 20 }, defaultRoutes: new[] { Route("0.0.0.0/0", interfaceIndex: 20) });

        Assert.Equal(LiveRouteTestEligibilityStatus.UnknownInterface, unknown.Status);
        Assert.Equal(LiveRouteTestEligibilityStatus.UnsafeInterface, defaultInterface.Status);
    }

    [Fact]
    public void MultipleOperationsAreRejected()
    {
        var first = Operation("route-a", "192.0.2.0/24", 1);
        var second = Operation("route-b", "198.51.100.0/24", 2);
        var preparation = Prepare(new[] { first, second }, new[] { Evidence(first), Evidence(second) });

        var result = ControlledLiveRouteTestValidator.Validate(preparation, SessionId, RestorationExecutionCapability.CreateForControlledTest(), SafeContext());

        Assert.Equal(LiveRouteTestEligibilityStatus.InvalidBatch, result.Status);
    }

    [Fact]
    public void WrongSessionAndInvalidCapabilityAreRejected()
    {
        var preparation = PrepareSingle();
        var wrongSession = ControlledLiveRouteTestValidator.Validate(preparation, Guid.NewGuid(), RestorationExecutionCapability.CreateForControlledTest(), SafeContext());
        var invalidCapability = ControlledLiveRouteTestValidator.Validate(preparation, SessionId, RestorationExecutionCapability.CreateInvalidForControlledTest(), SafeContext());

        Assert.Equal(LiveRouteTestEligibilityStatus.SessionMismatch, wrongSession.Status);
        Assert.Equal(LiveRouteTestEligibilityStatus.InvalidCapability, invalidCapability.Status);
    }

    [Fact]
    public void UnauthorizedOperationIsRejected()
    {
        var operation = Operation("route-a", "192.0.2.0/24", 1);
        var preparation = Prepare(new[] { operation }, Array.Empty<OwnershipEvidence>());

        var result = ControlledLiveRouteTestValidator.Validate(preparation, SessionId, RestorationExecutionCapability.CreateForControlledTest(), SafeContext());

        Assert.Equal(LiveRouteTestEligibilityStatus.BlockedByAuthorization, result.Status);
    }

    [Fact]
    public void EligibleResultIncludesDeterministicCommand()
    {
        var result = Validate(destination: "198.51.100.0/24", interfaceIndex: 7, metric: 23);

        Assert.True(result.IsEligible);
        Assert.Equal("198.51.100.0/24", result.Command!.Destination);
        Assert.Equal("0.0.0.0", result.Command.NextHop);
        Assert.Equal(7, result.Command.InterfaceIndex);
        Assert.Equal((uint)23, result.Command.Metric);
    }

    private static LiveRouteTestEligibility Validate(
        string destination = "192.0.2.0/24",
        string nextHop = "0.0.0.0",
        int interfaceIndex = 7,
        int metric = 50,
        IReadOnlyList<RouteState>? currentRoutes = null,
        IReadOnlyList<RouteState>? defaultRoutes = null,
        IReadOnlyList<string>? gatewayAddresses = null,
        IReadOnlyList<string>? dnsAddresses = null,
        IReadOnlyList<string>? localPrefixes = null,
        IReadOnlySet<int>? knownInterfaces = null)
    {
        var preparation = PrepareSingle(destination, nextHop, interfaceIndex, metric);
        return ControlledLiveRouteTestValidator.Validate(
            preparation,
            SessionId,
            RestorationExecutionCapability.CreateForControlledTest(),
            new LiveRouteTestContext(
                currentRoutes ?? Array.Empty<RouteState>(),
                defaultRoutes ?? Array.Empty<RouteState>(),
                gatewayAddresses ?? Array.Empty<string>(),
                dnsAddresses ?? Array.Empty<string>(),
                localPrefixes ?? Array.Empty<string>(),
                knownInterfaces ?? new HashSet<int> { interfaceIndex },
                new HashSet<int>()));
    }

    private static LiveRouteTestContext SafeContext()
        => new(Array.Empty<RouteState>(), Array.Empty<RouteState>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), new HashSet<int> { 7 }, new HashSet<int>());

    private static RestorationExecutionPreparation PrepareSingle(string destination = "192.0.2.0/24", string nextHop = "0.0.0.0", int interfaceIndex = 7, int metric = 50)
    {
        var operation = Operation("route-a", destination, 1, nextHop, interfaceIndex, metric);
        return Prepare(new[] { operation }, new[] { Evidence(operation) });
    }

    private static RestorationExecutionPreparation Prepare(IReadOnlyList<DryRunRestorationOperation> operations, IReadOnlyList<OwnershipEvidence> evidence)
    {
        var decisions = RestorationAuthorizationPolicy.AuthorizeBatch(
            new DryRunRestorationResult(operations, new DryRunRestorationSummary(operations.Count, operations.Count, operations.Count, 0, 0, true, Array.Empty<string>())),
            evidence,
            SessionId);
        return RestorationExecutionPreparation.Prepare(decisions, TransactionId, SessionId);
    }

    private static DryRunRestorationOperation Operation(string target, string destination, int order, string nextHop = "0.0.0.0", int interfaceIndex = 7, int metric = 50)
        => new(DryRunOperationCategory.Route, DryRunAction.RestoreBaseline, target, $"destination={destination};nextHop={nextHop};interfaceIndex={interfaceIndex};metric={metric}", "<absent>", "controlled-test", order, true, true, DryRunSafetyState.SafeToPlan);

    private static OwnershipEvidence Evidence(DryRunRestorationOperation operation)
        => new(SessionId, operation.Category, operation.TargetIdentity, operation.OriginalValue, operation.CurrentValue, Guid.NewGuid(), DateTimeOffset.UnixEpoch, operation.ExecutionOrder, OwnershipEvidenceSource.TestFixture, true);

    private static RouteState Route(string destination, string nextHop = "0.0.0.0", int interfaceIndex = 7, int metric = 50)
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = interfaceIndex, Metric = (uint)metric, AddressFamily = destination.Contains(':') ? "23" : "2" };
}
