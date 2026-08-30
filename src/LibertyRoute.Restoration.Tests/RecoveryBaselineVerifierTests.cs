using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Tests;

public sealed class RecoveryBaselineVerifierTests
{
    [Fact]
    public void ExactAuthorizedRouteBaselineIsVerifiedWhileUnrelatedRoutesAreIgnored()
    {
        var baselineRoute = Route("10.0.0.0/8", "10.0.0.1", 4, 1);
        var unrelated = Route("192.0.2.0/24", "192.0.2.1", 9, 5);
        var verifier = new RecoveryBaselineVerifier();

        var result = verifier.Verify(Snapshot(baselineRoute), Snapshot(baselineRoute, unrelated), Manifest(baselineRoute));

        Assert.True(result.IsVerified, result.Reason);
    }

    [Fact]
    public void AppliedValueStillPresentFailsClosed()
    {
        var baselineRoute = Route("10.0.0.0/8", "10.0.0.1", 4, 1);
        var applied = Route("10.0.0.0/8", "10.0.0.254", 4, 1);
        var result = new RecoveryBaselineVerifier().Verify(
            Snapshot(baselineRoute), Snapshot(baselineRoute, applied), Manifest(baselineRoute, applied));

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void UnexpectedThirdValueForAuthorizedTargetFailsClosed()
    {
        var baselineRoute = Route("10.0.0.0/8", "10.0.0.1", 4, 1);
        var unexpected = Route("10.0.0.0/8", "10.0.0.99", 4, 9);

        var result = new RecoveryBaselineVerifier().Verify(
            Snapshot(baselineRoute), Snapshot(baselineRoute, unexpected), Manifest(baselineRoute));

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void DuplicateAuthorizedTargetRepresentationFailsClosed()
    {
        var baselineRoute = Route("10.0.0.0/8", "10.0.0.1", 4, 1);

        var result = new RecoveryBaselineVerifier().Verify(
            Snapshot(baselineRoute), Snapshot(baselineRoute, baselineRoute), Manifest(baselineRoute));

        Assert.False(result.IsVerified);
    }

    [Fact]
    public void UnsupportedCategoryFailsClosed()
    {
        var route = Route("10.0.0.0/8", "10.0.0.1", 4, 1);
        var manifest = Manifest(route) with { OperationCategory = DryRunOperationCategory.Dns.ToString() };

        Assert.False(new RecoveryBaselineVerifier().Verify(Snapshot(route), Snapshot(route), manifest).IsVerified);
    }

    private static RecoveryManifest Manifest(RouteState baseline, RouteState? applied = null)
        => RecoveryManifest.Create(Guid.NewGuid(), Guid.NewGuid(), null, new string('A', 64),
            new[] { RecoveryEvidenceBinding.Create(Guid.NewGuid(), "evidence", new string('B', 64)) },
            Guid.NewGuid(), "operation", DryRunOperationCategory.Route.ToString(),
            RestorationPlanner.RouteIdentity(baseline), RestorationPlanner.RouteValue(baseline),
            RestorationPlanner.RouteValue(applied ?? Route("10.0.0.0/8", "10.0.0.254", 4, 1)), 1, new string('C', 64));

    private static NetworkStateSnapshot Snapshot(params RouteState[] routes)
        => new(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), routes, null);

    private static RouteState Route(string destination, string nextHop, int index, uint metric)
        => new() { Destination = destination, NextHop = nextHop, InterfaceIndex = index, Metric = metric, AddressFamily = "2" };
}
