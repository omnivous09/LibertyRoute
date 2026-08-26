using System.Reflection;
using LibertyRoute.Core;
using LibertyRoute.Restoration.Windows;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

/// <summary>
/// Synthetic Phase 4B tests for the mutation ownership recording coordinator.
/// Every provider is a fake; no native adapter, real provider, or network operation
/// is ever constructed or invoked here.
/// </summary>
public sealed class MutationOwnershipCoordinatorTests : IAsyncLifetime
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string RouteValue = "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1";

    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.MutationCoordinator", Guid.NewGuid().ToString("N"));
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

    private FileOwnershipLedger NewLedger() => new(_root);

    private sealed record RequestFixture(AuthorizedRestorationRequest Request, DryRunRestorationOperation Operation);

    private static RequestFixture Fixture(Guid sessionId, Guid transactionId, string target = "route-10.0.0.0/24")
    {
        var operation = new DryRunRestorationOperation(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            target,
            RouteValue,
            "<absent>",
            "baseline route missing",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

        var evidence = new OwnershipEvidence(
            sessionId,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.MutationLedger,
            true);

        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, sessionId);
        var request = AuthorizedRestorationRequest.Create(operation, authorization, transactionId, sessionId);
        return new RequestFixture(request, operation);
    }

    private sealed class FakeProvider : IRestorationMutationProvider
    {
        private readonly Func<CancellationToken, Task<RestorationMutationResult>> _handler;
        private int _callCount;

        public FakeProvider(Func<CancellationToken, Task<RestorationMutationResult>> handler)
            => _handler = handler;

        public int CallCount => Volatile.Read(ref _callCount);
        public List<AuthorizedRestorationRequest> Requests { get; } = new();

        public async Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Requests.Add(request);
            return await _handler(cancellationToken);
        }

        public static FakeProvider Returning(RestorationMutationState state)
            => new(_ => Task.FromResult(Result(state)));

        public static FakeProvider Throwing(Exception exception)
            => new(_ => throw exception);

        public static RestorationMutationResult Result(RestorationMutationState state)
            => new("synthetic-operation", state, $"synthetic {state}", false);
    }

    private sealed class ScriptableLedger : IOwnershipLedger
    {
        private readonly FileOwnershipLedger _inner;

        public ScriptableLedger(string root)
            => _inner = new FileOwnershipLedger(root);

        public Func<PersistedOwnedChange, Exception?>? AppendInterceptor { get; set; }
        public List<PersistedOwnedChange> AppendCalls { get; } = new();

        public async Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken)
        {
            AppendCalls.Add(record);
            if (AppendInterceptor?.Invoke(record) is { } failure)
                throw failure;

            await _inner.AppendAsync(record, cancellationToken);
        }

        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => _inner.ReadForSessionAsync(sessionId, cancellationToken);

        public Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => _inner.ClearSessionAsync(sessionId, cancellationToken);

        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken)
            => _inner.ExistsAsync(sessionId, changeId, cancellationToken);
    }

    // --- ChangeId derivation ---

    [Fact]
    public void ChangeIdIsDeterministicForSameSessionAndOperationIdentity()
    {
        var first = MutationOwnershipCoordinator.DeriveChangeId(SessionId, "Route|RestoreBaseline|route-x|value|<absent>");
        var second = MutationOwnershipCoordinator.DeriveChangeId(SessionId, "Route|RestoreBaseline|route-x|value|<absent>");
        Assert.Equal(first, second);
    }

    [Fact]
    public void TransactionIdDoesNotAffectChangeId()
    {
        var first = Fixture(SessionId, Guid.NewGuid());
        var second = Fixture(SessionId, Guid.NewGuid());
        Assert.NotEqual(first.Request.TransactionId, second.Request.TransactionId);
        Assert.Equal(
            MutationOwnershipCoordinator.DeriveChangeId(SessionId, first.Request.OperationIdentity),
            MutationOwnershipCoordinator.DeriveChangeId(SessionId, second.Request.OperationIdentity));
    }

    [Fact]
    public void DifferentSessionProducesDifferentChangeId()
    {
        var identity = "Route|RestoreBaseline|route-x|value|<absent>";
        Assert.NotEqual(
            MutationOwnershipCoordinator.DeriveChangeId(SessionId, identity),
            MutationOwnershipCoordinator.DeriveChangeId(OtherSessionId, identity));
    }

    [Fact]
    public void DifferentOperationProducesDifferentChangeId()
    {
        Assert.NotEqual(
            MutationOwnershipCoordinator.DeriveChangeId(SessionId, "Route|RestoreBaseline|route-a|value|<absent>"),
            MutationOwnershipCoordinator.DeriveChangeId(SessionId, "Route|RestoreBaseline|route-b|value|<absent>"));
    }

    // --- Happy path ---

    [Fact]
    public async Task SuccessfulExecutionPersistsPlannedThenAppliedInOrder()
    {
        var ledger = new ScriptableLedger(_root);
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, result.Outcome);
        Assert.True(result.PlannedPersisted);
        Assert.True(result.AppliedPersisted);
        Assert.False(result.RequiresManualRecovery);
        Assert.Equal(2, ledger.AppendCalls.Count);
        Assert.Equal(OwnedChangeLifecycle.Planned, ledger.AppendCalls[0].Lifecycle);
        Assert.Equal(OwnedChangeLifecycle.Applied, ledger.AppendCalls[1].Lifecycle);
        Assert.Equal(ledger.AppendCalls[0].ChangeId, ledger.AppendCalls[1].ChangeId);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Applied, stored.Lifecycle);
        Assert.True(stored.IsComplete);
        Assert.Equal(result.ChangeId, stored.ChangeId);
    }

    [Fact]
    public async Task PlannedRecordExistsBeforeProviderInvocation()
    {
        PersistedOwnedChange[] observedDuringProviderCall = Array.Empty<PersistedOwnedChange>();
        var ledger = new ScriptableLedger(_root);
        var provider = new FakeProvider(async _ =>
        {
            observedDuringProviderCall = (await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToArray();
            return FakeProvider.Result(RestorationMutationState.Succeeded);
        });
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        var planned = Assert.Single(observedDuringProviderCall);
        Assert.Equal(OwnedChangeLifecycle.Planned, planned.Lifecycle);
        Assert.False(planned.IsComplete);
    }

    [Fact]
    public async Task ProviderReceivesExactlyTheAuthorizedRequest()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Same(fixture.Request, Assert.Single(provider.Requests));
    }

    // --- Provider outcome mapping ---

    [Theory]
    [InlineData(RestorationMutationState.Failed, RecordedMutationOutcome.MutationFailedRemainsPlanned)]
    [InlineData(RestorationMutationState.Unsupported, RecordedMutationOutcome.ProviderUnsupportedRemainsPlanned)]
    [InlineData(RestorationMutationState.Skipped, RecordedMutationOutcome.ProviderSkippedRemainsPlanned)]
    [InlineData(RestorationMutationState.StateChangedExternally, RecordedMutationOutcome.ProviderStateChangedExternallyRemainsPlanned)]
    [InlineData(RestorationMutationState.AlreadyRestored, RecordedMutationOutcome.AlreadyRestoredOwnershipNotClaimedRemainsPlanned)]
    public async Task NonSucceededProviderStatesLeavePlannedWithoutApplied(
        RestorationMutationState state,
        RecordedMutationOutcome expectedOutcome)
    {
        var ledger = new ScriptableLedger(_root);
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(state));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.True(result.PlannedPersisted);
        Assert.False(result.AppliedPersisted);
        Assert.NotNull(result.ProviderResult);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Planned, stored.Lifecycle);
        Assert.False(stored.IsComplete);
    }

    [Fact]
    public async Task ProviderThrowLeavesPlannedAndRequiresManualRecovery()
    {
        var ledger = new ScriptableLedger(_root);
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Throwing(new InvalidOperationException("native failure")));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ProviderThrewMutationIndeterminateManualRecoveryRequired, result.Outcome);
        Assert.True(result.RequiresManualRecovery);
        Assert.Contains("InvalidOperationException", result.Reason, StringComparison.Ordinal);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Planned, stored.Lifecycle);
    }

    // --- Failure semantics ---

    [Fact]
    public async Task PlannedWriteFailurePreventsProviderInvocation()
    {
        var ledger = new ScriptableLedger(_root)
        {
            AppendInterceptor = record => record.Lifecycle == OwnedChangeLifecycle.Planned
                ? new IOException("simulated disk failure")
                : null
        };
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.PlannedWriteFailedMutationNotAttempted, result.Outcome);
        Assert.Equal(0, provider.CallCount);
        Assert.False(result.PlannedPersisted);
        Assert.Contains("IOException", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppliedPersistenceFailureAfterSuccessReturnsDistinctManualRecoveryResult()
    {
        var ledger = new ScriptableLedger(_root)
        {
            AppendInterceptor = record => record.Lifecycle == OwnedChangeLifecycle.Applied
                ? new IOException("simulated disk failure")
                : null
        };
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.MutationSucceededOwnershipRecordingFailed, result.Outcome);
        Assert.True(result.RequiresManualRecovery);
        Assert.True(result.PlannedPersisted);
        Assert.False(result.AppliedPersisted);
        Assert.NotNull(result.ProviderResult);
        Assert.Equal(RestorationMutationState.Succeeded, result.ProviderResult!.State);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Planned, stored.Lifecycle);
    }

    // --- Session binding ---

    [Fact]
    public async Task WrongSessionRejectedWithZeroSideEffects()
    {
        var ledger = new ScriptableLedger(_root);
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(OtherSessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.SessionMismatchRejectedNoSideEffects, result.Outcome);
        Assert.False(result.PlannedPersisted);
        Assert.False(result.AppliedPersisted);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(ledger.AppendCalls);
        Assert.Empty(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Empty(await ledger.ReadForSessionAsync(OtherSessionId, CancellationToken.None));
    }

    // --- Retry / duplicate execution guards ---

    [Fact]
    public async Task RetryAfterAppliedReturnsAlreadyAppliedWithoutProviderCall()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var first = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        var second = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, first.Outcome);
        Assert.Equal(RecordedMutationOutcome.AlreadyAppliedNoProviderCall, second.Outcome);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task RetryWithExistingPlannedIsBlockedAndRequiresManualRecovery()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Failed);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var first = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        var second = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.MutationFailedRemainsPlanned, first.Outcome);
        Assert.Equal(RecordedMutationOutcome.ExistingPlannedBlockedManualRecoveryRequired, second.Outcome);
        Assert.True(second.RequiresManualRecovery);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task ExecutionAfterRevertedIsRejectedWithoutProviderCall()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var executed = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        var reverted = await coordinator.RecordRevertedAsync(SessionId, executed.ChangeId, CancellationToken.None);
        var reExecuted = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.RevertedRecordingSucceeded, reverted.Outcome);
        Assert.Equal(RecordedMutationOutcome.ExistingRevertedRejectedNoProviderCall, reExecuted.Outcome);
        Assert.Equal(1, provider.CallCount);
    }

    // --- Reverted recording ---

    [Fact]
    public async Task RecordRevertedAdvancesAppliedPreservingImmutableFields()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var executed = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        var before = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));

        var result = await coordinator.RecordRevertedAsync(SessionId, executed.ChangeId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.RevertedRecordingSucceeded, result.Outcome);
        var after = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Reverted, after.Lifecycle);
        Assert.False(after.IsComplete);
        Assert.Equal(before.SessionId, after.SessionId);
        Assert.Equal(before.ChangeId, after.ChangeId);
        Assert.Equal(before.Category, after.Category);
        Assert.Equal(before.TargetIdentity, after.TargetIdentity);
        Assert.Equal(before.OriginalValue, after.OriginalValue);
        Assert.Equal(before.AppliedValue, after.AppliedValue);
        Assert.Equal(before.RecordedAtUtc, after.RecordedAtUtc);
        Assert.Equal(before.SequenceNumber, after.SequenceNumber);
        Assert.Equal(before.EvidenceSource, after.EvidenceSource);
    }

    [Fact]
    public async Task RevertOfMissingChangeIdFailsClosed()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));

        var result = await coordinator.RecordRevertedAsync(SessionId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.RevertedTargetNotFoundNoLedgerChange, result.Outcome);
        Assert.Empty(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task RevertOfNonAppliedRecordFailsClosed()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Failed));
        var fixture = Fixture(SessionId, TransactionId);
        await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        var result = await coordinator.RecordRevertedAsync(SessionId, stored.ChangeId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.RevertedRequiresAppliedRecordNoLedgerChange, result.Outcome);
        Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    // --- Cancellation ---

    [Fact]
    public async Task PreCancelledExecutionPropagatesWithZeroProviderCallsAndEmptyLedger()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, cts.Token));

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterProviderSuccessStillPersistsApplied()
    {
        var ledger = NewLedger();
        using var cts = new CancellationTokenSource();
        var provider = new FakeProvider(_ =>
        {
            cts.Cancel();
            return Task.FromResult(FakeProvider.Result(RestorationMutationState.Succeeded));
        });
        var coordinator = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, cts.Token);

        Assert.True(cts.Token.IsCancellationRequested);
        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, result.Outcome);
        Assert.True(result.AppliedPersisted);
        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Applied, stored.Lifecycle);
    }

    // --- Concurrency ---

    [Fact]
    public async Task ConcurrentDuplicateExecutionAcrossCoordinatorInstancesInvokesProviderExactlyOnce()
    {
        var ledger = NewLedger();
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async cancellationToken =>
        {
            providerEntered.TrySetResult();
            await releaseProvider.Task.WaitAsync(cancellationToken);
            return FakeProvider.Result(RestorationMutationState.Succeeded);
        });

        var coordinatorA = new MutationOwnershipCoordinator(ledger, provider);
        var coordinatorB = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var first = coordinatorA.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        await providerEntered.Task; // Deterministic: the first execution is inside the provider.
        var second = coordinatorB.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        await Task.Delay(150); // Scheduling grace so the second reaches the execution gate; outcome-invariant.
        releaseProvider.SetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, firstResult.Outcome);
        Assert.Equal(RecordedMutationOutcome.AlreadyAppliedNoProviderCall, secondResult.Outcome);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentWaiterAfterProviderFailureIsBlockedFailClosed()
    {
        var ledger = NewLedger();
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async cancellationToken =>
        {
            providerEntered.TrySetResult();
            await releaseProvider.Task.WaitAsync(cancellationToken);
            return FakeProvider.Result(RestorationMutationState.Failed);
        });

        var coordinatorA = new MutationOwnershipCoordinator(ledger, provider);
        var coordinatorB = new MutationOwnershipCoordinator(ledger, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var first = coordinatorA.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        await providerEntered.Task;
        var second = coordinatorB.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        await Task.Delay(150);
        releaseProvider.SetResult();

        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(RecordedMutationOutcome.MutationFailedRemainsPlanned, firstResult.Outcome);
        Assert.Equal(RecordedMutationOutcome.ExistingPlannedBlockedManualRecoveryRequired, secondResult.Outcome);
        Assert.True(secondResult.RequiresManualRecovery);
        Assert.Equal(1, provider.CallCount);
        Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    // --- Authorization integration ---

    [Fact]
    public async Task CoordinatorEvidenceAuthorizesExactRestorationOperation()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        var decision = RestorationAuthorizationPolicy.Authorize(fixture.Operation, new[] { evidence }, SessionId);
        Assert.Equal(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    [Fact]
    public async Task PlannedOnlyEvidenceFromFailedMutationCannotAuthorize()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Failed));
        var fixture = Fixture(SessionId, TransactionId);

        await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        Assert.False(evidence.IsComplete);
        var decision = RestorationAuthorizationPolicy.Authorize(fixture.Operation, new[] { evidence }, SessionId);
        Assert.NotEqual(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    [Fact]
    public async Task AppliedPersistenceFailureEvidenceCannotAuthorizeAndRequiresManualRecovery()
    {
        var ledger = new ScriptableLedger(_root)
        {
            AppendInterceptor = record => record.Lifecycle == OwnedChangeLifecycle.Applied
                ? new IOException("simulated disk failure")
                : null
        };
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.MutationSucceededOwnershipRecordingFailed, result.Outcome);
        Assert.True(result.RequiresManualRecovery);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        Assert.False(evidence.IsComplete);
        var decision = RestorationAuthorizationPolicy.Authorize(fixture.Operation, new[] { evidence }, SessionId);
        Assert.NotEqual(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    [Fact]
    public async Task RevertedEvidenceCannotAuthorize()
    {
        var ledger = NewLedger();
        var coordinator = new MutationOwnershipCoordinator(ledger, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var executed = await coordinator.ExecuteAuthorizedMutationAsync(fixture.Request, SessionId, CancellationToken.None);
        await coordinator.RecordRevertedAsync(SessionId, executed.ChangeId, CancellationToken.None);

        var evidence = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToOwnershipEvidence();
        Assert.False(evidence.IsComplete);
        var decision = RestorationAuthorizationPolicy.Authorize(fixture.Operation, new[] { evidence }, SessionId);
        Assert.NotEqual(OperationAuthorizationStatus.Authorized, decision.Status);
    }

    // --- Surface / security guards ---

    [Fact]
    public void BridgeSurfaceExposesNoNetworkMutationOperations()
    {
        var forbiddenNameParts = new[]
        {
            "addroute", "deleteroute", "createipforward", "deleteipforward", "setipforward",
            "executemutation", "commitmutation", "connect", "disconnect"
        };
        var forbiddenParameterTypes = new[]
        {
            typeof(NetworkStateSnapshot),
            typeof(IRouteMutationNative),
            typeof(RouteRestorationCommand),
            typeof(DryRunRestorationOperation),
            typeof(DryRunRestorationResult)
        };

        var types = new[]
        {
            typeof(MutationOwnershipCoordinator),
            typeof(MutationExecutionGate),
            typeof(IOwnershipLedger)
        };
        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            Assert.NotEmpty(methods);
            foreach (var method in methods)
            {
                var name = method.Name.ToLowerInvariant();
                Assert.DoesNotContain(forbiddenNameParts, part => name.Contains(part, StringComparison.Ordinal));
                Assert.All(method.GetParameters(), parameter => Assert.DoesNotContain(parameter.ParameterType, forbiddenParameterTypes));
            }
        }
    }

    [Fact]
    public void BridgeResultModelContainsNoSecretLikeMembers()
    {
        var forbiddenNameParts = new[]
        {
            "password", "passwd", "secret", "token", "credential", "certificate", "apikey", "privatekey"
        };
        var allowedPropertyTypes = new[]
        {
            typeof(string),
            typeof(Guid),
            typeof(bool),
            typeof(RecordedMutationOutcome),
            typeof(RestorationMutationResult)
        };

        var properties = typeof(RecordedMutationExecution).GetProperties();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            var name = property.Name.ToLowerInvariant();
            Assert.DoesNotContain(forbiddenNameParts, part => name.Contains(part, StringComparison.Ordinal));
            Assert.Contains(property.PropertyType, allowedPropertyTypes);
        }
    }
}