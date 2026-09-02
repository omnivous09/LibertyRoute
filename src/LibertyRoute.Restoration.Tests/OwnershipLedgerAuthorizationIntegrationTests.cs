using Xunit;
using System.Net;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration.Tests;

/// <summary>
/// Synthetic integration tests proving that ownership-ledger records map into
/// OwnershipEvidence and drive the Phase 3C authorization policy exactly as
/// hand-built evidence does. No provider, native adapter, or network operation
/// is involved anywhere in this file.
/// </summary>
public sealed class OwnershipLedgerAuthorizationIntegrationTests : IAsyncLifetime
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string RouteValue = "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1";
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.OwnershipLedgerAuth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test run.
        }

        await Task.CompletedTask;
    }

    private static DryRunRestorationOperation BaselineRouteOperation()
        => new(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            "route-10.0.0.0/24",
            RouteValue,
            "<absent>",
            "A baseline route is missing and may require restoration.",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

    private static PersistedOwnedChange LedgerRecordFor(
        DryRunRestorationOperation operation,
        OwnedChangeLifecycle lifecycle,
        Guid? sessionId = null,
        string? appliedValueOverride = null,
        Guid? changeId = null)
        => PersistedOwnedChange.Create(
            sessionId ?? SessionId,
            changeId ?? Guid.NewGuid(),
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            appliedValueOverride ?? operation.CurrentValue,
            RecordedAt,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.MutationLedger,
            lifecycle);

    private static PersistedOwnedChange ExactLedgerRecordFor(DryRunRestorationOperation operation)
        => PersistedOwnedChange.CreateExactRoute(SessionId, Guid.NewGuid(), operation.Category,
            operation.TargetIdentity, operation.OriginalValue, operation.CurrentValue, RecordedAt,
            operation.ExecutionOrder, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied,
            new ExactRouteMutationIdentity(1,
                NativeRouteKey.Create(NativeRouteAddressFamily.IPv4, IPAddress.Parse("192.0.2.0"), 24,
                    IPAddress.Parse("192.0.2.1"), 42),
                new NativeRouteProfile(24, 3600, 1800, 5, 3, false, false, false, false)),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Fact]
    public async Task AppliedLedgerEvidenceAuthorizesExactMatchingOperation()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Applied), CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);
        Assert.NotNull(decision.MatchedEvidence);
        Assert.True(decision.FutureAutomaticExecutionAllowed);
    }

    [Fact]
    public async Task AppliedLedgerEvidenceProducesExecutablePreparationWithoutExecution()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Applied), CancellationToken.None);

        var dryRun = new DryRunRestorationResult(
            new[] { operation },
            new DryRunRestorationSummary(1, 1, 1, 0, 0, true, Array.Empty<string>()));
        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        var batch = RestorationAuthorizationPolicy.AuthorizeBatch(dryRun, new[] { evidence }, SessionId);
        var preparation = RestorationExecutionPreparation.Prepare(batch, Guid.NewGuid(), SessionId);

        Assert.True(preparation.CanExecuteAutomatically);
        Assert.Single(preparation.AuthorizedRequests);
        Assert.Empty(preparation.RejectedOperations);
    }

    [Fact]
    public async Task PlannedLedgerEvidenceDoesNotAuthorize()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Planned), CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        Assert.False(evidence.IsComplete);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);
        Assert.NotEqual(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    [Fact]
    public async Task AppliedThenRevertedLedgerEvidenceMapsIncompleteAndIsDenied()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        var changeId = Guid.NewGuid();

        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Planned, changeId: changeId), CancellationToken.None);
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Applied, changeId: changeId), CancellationToken.None);
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Reverted, changeId: changeId), CancellationToken.None);

        Assert.Equal(changeId, Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ChangeId);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Reverted, stored.Lifecycle);

        var evidence = stored.ToOwnershipEvidence();
        Assert.False(evidence.IsComplete);

        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);
        Assert.NotEqual(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    [Fact]
    public async Task WrongSessionLedgerEvidenceDoesNotAuthorize()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        var otherSession = Guid.NewGuid();
        await ledger.AppendAsync(LedgerRecordFor(operation, OwnedChangeLifecycle.Applied, sessionId: otherSession), CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(otherSession, CancellationToken.None));
        Assert.Equal(otherSession, stored.SessionId);
        var evidence = stored.ToOwnershipEvidence();
        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedNoOwnership, decision.Status);
    }

    [Fact]
    public async Task AlteredAppliedValueLedgerEvidenceDoesNotAuthorize()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        await ledger.AppendAsync(
            LedgerRecordFor(operation, OwnedChangeLifecycle.Applied, appliedValueOverride: "destination=192.0.2.0/24"),
            CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        var decision = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);

        Assert.Equal(OperationAuthorizationStatus.DeniedOwnershipMismatch, decision.Status);
    }

    [Fact]
    public async Task ExactStructuralEvidenceDoesNotCreateASeparateGenericAuthorizationPath()
    {
        var ledger = new FileOwnershipLedger(_root);
        var operation = BaselineRouteOperation();
        await ledger.AppendAsync(ExactLedgerRecordFor(operation), CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.True(stored.HasValidAppliedExactRouteOwnershipEvidence);
        var decision = RestorationAuthorizationPolicy.Authorize(
            operation, new[] { stored.ToOwnershipEvidence() }, SessionId);
        Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);

        var tampered = stored with { ExactRouteEvidenceFingerprint = new string('0', 64) };
        Assert.False(tampered.HasValidAppliedExactRouteOwnershipEvidence);
        Assert.Equal(stored.ToOwnershipEvidence(), tampered.ToOwnershipEvidence());
    }

}
