using System.Collections.Concurrent;
using LibertyRoute.Core;

namespace LibertyRoute.Restoration;

/// <summary>
/// Process-wide execution serialization for owned mutations. For each
/// (SessionId, ChangeId) pair at most one coordinator execution may be between
/// "ledger decision" and "provider completion" at any time, regardless of how many
/// coordinator instances exist. Gates are process-lifetime and never removed or
/// disposed while waiters may exist. Different sessions and different operations
/// remain independently concurrent; there is deliberately no global lock.
///
/// Lock ordering is always execution gate first, ledger gate second; the ledger never
/// calls back into the coordinator, so no deadlock cycle exists.
/// </summary>
internal static class MutationExecutionGate
{
    private static readonly ConcurrentDictionary<(Guid SessionId, Guid ChangeId), SemaphoreSlim> Gates = new();

    public static SemaphoreSlim GateFor(Guid sessionId, Guid changeId)
        => Gates.GetOrAdd((sessionId, changeId), static key => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Terminal outcome of one coordinator operation. Outcomes are explicit so callers can
/// never confuse "mutation succeeded" with "ownership evidence is complete".
/// </summary>
public enum RecordedMutationOutcome
{
    ExecutedAndApplied,
    AlreadyAppliedNoProviderCall,
    ExistingPlannedBlockedManualRecoveryRequired,
    ExistingRevertedRejectedNoProviderCall,
    MutationFailedRemainsPlanned,
    ProviderUnsupportedRemainsPlanned,
    ProviderSkippedRemainsPlanned,
    ProviderStateChangedExternallyRemainsPlanned,
    AlreadyRestoredOwnershipNotClaimedRemainsPlanned,
    ProviderThrewMutationIndeterminateManualRecoveryRequired,
    PlannedWriteFailedMutationNotAttempted,
    MutationSucceededOwnershipRecordingFailed,
    SessionMismatchRejectedNoSideEffects,
    RevertedRecordingSucceeded,
    RevertedTargetNotFoundNoLedgerChange,
    RevertedRequiresAppliedRecordNoLedgerChange,
    RevertedPersistenceFailedManualRecoveryRequired
}

/// <summary>
/// Structured result of one coordinator operation. Contains operational metadata only;
/// it never contains secrets.
/// </summary>
public sealed record RecordedMutationExecution(
    string OperationIdentity,
    Guid ChangeId,
    Guid SessionId,
    RecordedMutationOutcome Outcome,
    RestorationMutationResult? ProviderResult,
    bool PlannedPersisted,
    bool AppliedPersisted,
    bool RequiresManualRecovery,
    string Reason);

/// <summary>
/// Mandatory recording bridge between authorized restoration requests and mutation
/// providers. Future mutation code cannot reach the provider without passing through
/// this sequence:
///
/// 1. bind the request to the active session (reject otherwise, zero side effects);
/// 2. derive the deterministic ChangeId from (SessionId, OperationIdentity);
/// 3. acquire the process-wide per-(SessionId, ChangeId) execution gate;
/// 4. re-read the ledger and evaluate the persisted lifecycle:
///      none       -> append Planned, then invoke the provider;
///      Applied    -> AlreadyAppliedNoProviderCall (provider ran exactly once ever);
///      Reverted   -> fail-closed rejection, no provider call;
///      Planned    -> fail-closed block, no provider call: a bare Planned record cannot
///                    distinguish never-attempted, definitively-failed, and possibly-
///                    succeeded attempts, so automatic re-execution is prohibited and
///                    explicit recovery is required (this rule derives purely from
///                    persisted state and therefore survives process crashes);
/// 5. only a provider result of Succeeded transitions Planned to Applied, persisted
///    through an internal non-cancelable completion path: once a real mutation has
///    succeeded, the ownership gap must not be reopened by late cancellation;
/// 6. every other provider outcome (including AlreadyRestored) leaves the record as
///    Planned; presence of state never proves LibertyRoute ownership;
/// 7. a provider exception leaves Planned and reports an indeterminate mutation that
///    requires manual recovery.
///
/// No automatic rollback and no automatic retry are performed by this coordinator.
/// </summary>
public sealed class MutationOwnershipCoordinator
{
    private readonly IOwnershipLedger _ledger;
    private readonly IRestorationMutationProvider _provider;

    public MutationOwnershipCoordinator(IOwnershipLedger ledger, IRestorationMutationProvider provider)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>
    /// Derives the stable ChangeId for an owned mutation from exactly
    /// (SessionId, "\n", OperationIdentity). Both inputs are immutable across retries:
    /// SessionId is the canonical ownership transaction identity and OperationIdentity
    /// is the authorization-bound invariant of the exact operation. The ChangeId is the
    /// raw first 16 bytes of the SHA-256 digest represented as a GUID; no random
    /// component participates, so the same intended operation always yields the same
    /// ChangeId and the ledger lifecycle rules make retries idempotent.
    /// </summary>
    public static Guid DeriveChangeId(Guid sessionId, string operationIdentity)
        => OwnershipIdentity.DeriveChangeId(sessionId, operationIdentity);

    public async Task<RecordedMutationExecution> ExecuteAuthorizedMutationAsync(
        AuthorizedRestorationRequest request,
        Guid activeSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (activeSessionId == Guid.Empty)
            throw new ArgumentException("Active session id is required.", nameof(activeSessionId));

        if (request.SessionId != activeSessionId)
        {
            return Result(
                request.OperationIdentity, DeriveChangeId(request.SessionId, request.OperationIdentity), request.SessionId,
                RecordedMutationOutcome.SessionMismatchRejectedNoSideEffects,
                providerResult: null,
                plannedPersisted: false, appliedPersisted: false, requiresManualRecovery: false,
                "The request session does not match the active session; no ledger write and no provider call were made.");
        }

        var changeId = DeriveChangeId(activeSessionId, request.OperationIdentity);
        var gate = MutationExecutionGate.GateFor(activeSessionId, changeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindRecordAsync(activeSessionId, changeId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.Lifecycle switch
                {
                    OwnedChangeLifecycle.Applied => Result(
                        request.OperationIdentity, changeId, activeSessionId,
                        RecordedMutationOutcome.AlreadyAppliedNoProviderCall,
                        providerResult: null,
                        plannedPersisted: true, appliedPersisted: true, requiresManualRecovery: false,
                        "This operation is already recorded as Applied; the provider was not invoked again."),
                    OwnedChangeLifecycle.Reverted => Result(
                        request.OperationIdentity, changeId, activeSessionId,
                        RecordedMutationOutcome.ExistingRevertedRejectedNoProviderCall,
                        providerResult: null,
                        plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: false,
                        "This operation is recorded as Reverted; re-execution requires a new explicitly recorded intent."),
                    _ => Result(
                        request.OperationIdentity, changeId, activeSessionId,
                        RecordedMutationOutcome.ExistingPlannedBlockedManualRecoveryRequired,
                        providerResult: null,
                        plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: true,
                        "A Planned ownership record already exists. The ledger cannot distinguish a never-attempted, definitively-failed, or possibly-succeeded attempt, so automatic re-execution is prohibited and explicit recovery is required."),
                };
            }

            var recordedAtUtc = DateTimeOffset.UtcNow;
            var plannedRecord = BuildOwnedRecord(request, activeSessionId, changeId, OwnedChangeLifecycle.Planned, recordedAtUtc);
            try
            {
                await _ledger.AppendAsync(plannedRecord, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation before the provider was invoked. The Planned append is
                // atomic: it either persisted completely or not at all.
                throw;
            }
            catch (Exception exception)
            {
                return Result(
                    request.OperationIdentity, changeId, activeSessionId,
                    RecordedMutationOutcome.PlannedWriteFailedMutationNotAttempted,
                    providerResult: null,
                    plannedPersisted: false, appliedPersisted: false, requiresManualRecovery: false,
                    $"The Planned ownership record could not be persisted ({exception.GetType().Name}: {exception.Message}); the provider was not invoked.");
            }

            RestorationMutationResult providerResult;
            try
            {
                providerResult = await _provider.ApplyAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var cancellationNote = cancellationToken.IsCancellationRequested
                    ? " The provider aborted during caller cancellation."
                    : string.Empty;
                return Result(
                    request.OperationIdentity, changeId, activeSessionId,
                    RecordedMutationOutcome.ProviderThrewMutationIndeterminateManualRecoveryRequired,
                    providerResult: null,
                    plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: true,
                    $"The provider threw {exception.GetType().Name}: {exception.Message}.{cancellationNote} The mutation state is indeterminate; the Planned record is retained and automatic retry is prohibited.");
            }

            if (providerResult.State != RestorationMutationState.Succeeded)
            {
                var outcome = providerResult.State switch
                {
                    RestorationMutationState.AlreadyRestored => RecordedMutationOutcome.AlreadyRestoredOwnershipNotClaimedRemainsPlanned,
                    RestorationMutationState.Unsupported => RecordedMutationOutcome.ProviderUnsupportedRemainsPlanned,
                    RestorationMutationState.Skipped => RecordedMutationOutcome.ProviderSkippedRemainsPlanned,
                    RestorationMutationState.StateChangedExternally => RecordedMutationOutcome.ProviderStateChangedExternallyRemainsPlanned,
                    _ => RecordedMutationOutcome.MutationFailedRemainsPlanned
                };

                var alreadyRestoredNote = providerResult.State == RestorationMutationState.AlreadyRestored
                    ? " The state was already present, which does not prove LibertyRoute ownership."
                    : string.Empty;

                return Result(
                    request.OperationIdentity, changeId, activeSessionId,
                    outcome,
                    providerResult,
                    plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: false,
                    $"The provider reported {providerResult.State}; no Applied ownership was recorded.{alreadyRestoredNote}");
            }

            var appliedRecord = plannedRecord with
            {
                Lifecycle = OwnedChangeLifecycle.Applied,
                IsComplete = true
            };

            try
            {
                // Internal non-cancelable completion path: once the provider reported
                // success, the ownership gap must not be reopened by late cancellation.
                await _ledger.AppendAsync(appliedRecord, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Result(
                    request.OperationIdentity, changeId, activeSessionId,
                    RecordedMutationOutcome.MutationSucceededOwnershipRecordingFailed,
                    providerResult,
                    plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: true,
                    $"The mutation succeeded but the Applied ownership transition could not be persisted ({exception.GetType().Name}: {exception.Message}). The mutation must not be treated as safely owned; manual recovery is required.");
            }

            return Result(
                request.OperationIdentity, changeId, activeSessionId,
                RecordedMutationOutcome.ExecutedAndApplied,
                providerResult,
                plannedPersisted: true, appliedPersisted: true, requiresManualRecovery: false,
                "The provider succeeded and the owned change is recorded as Applied.");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Explicitly records Applied to Reverted for one exact owned change. The existing
    /// Applied record is loaded from the ledger and advanced in place; callers cannot
    /// supply or alter ownership metadata. No network rollback is performed here.
    /// </summary>
    public async Task<RecordedMutationExecution> RecordRevertedAsync(
        Guid sessionId,
        Guid changeId,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (changeId == Guid.Empty)
            throw new ArgumentException("Change id is required.", nameof(changeId));

        var gate = MutationExecutionGate.GateFor(sessionId, changeId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindRecordAsync(sessionId, changeId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Result(
                    string.Empty, changeId, sessionId,
                    RecordedMutationOutcome.RevertedTargetNotFoundNoLedgerChange,
                    providerResult: null,
                    plannedPersisted: false, appliedPersisted: false, requiresManualRecovery: false,
                    "No ownership record exists for this change id; nothing was reverted or changed.");
            }

            if (existing.Lifecycle != OwnedChangeLifecycle.Applied)
            {
                return Result(
                    string.Empty, changeId, sessionId,
                    RecordedMutationOutcome.RevertedRequiresAppliedRecordNoLedgerChange,
                    providerResult: null,
                    plannedPersisted: existing.Lifecycle == OwnedChangeLifecycle.Planned,
                    appliedPersisted: false,
                    requiresManualRecovery: false,
                    $"Only an Applied record can be reverted; the existing record is {existing.Lifecycle}. Nothing was changed.");
            }

            var reverted = existing with
            {
                Lifecycle = OwnedChangeLifecycle.Reverted,
                IsComplete = false
            };

            try
            {
                await _ledger.AppendAsync(reverted, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Result(
                    string.Empty, changeId, sessionId,
                    RecordedMutationOutcome.RevertedPersistenceFailedManualRecoveryRequired,
                    providerResult: null,
                    plannedPersisted: true, appliedPersisted: true, requiresManualRecovery: true,
                    $"The Applied record could not be transitioned to Reverted ({exception.GetType().Name}: {exception.Message}); the record remains Applied and requires manual attention.");
            }

            return Result(
                string.Empty, changeId, sessionId,
                RecordedMutationOutcome.RevertedRecordingSucceeded,
                providerResult: null,
                plannedPersisted: true, appliedPersisted: false, requiresManualRecovery: false,
                "The owned change is now recorded as Reverted; its evidence is incomplete and can no longer authorize restoration.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PersistedOwnedChange?> FindRecordAsync(
        Guid sessionId,
        Guid changeId,
        CancellationToken cancellationToken)
    {
        var records = await _ledger.ReadForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return records.FirstOrDefault(record => record.ChangeId == changeId);
    }

    private static PersistedOwnedChange BuildOwnedRecord(
        AuthorizedRestorationRequest request,
        Guid activeSessionId,
        Guid changeId,
        OwnedChangeLifecycle lifecycle,
        DateTimeOffset recordedAtUtc)
        => PersistedOwnedChange.Create(
            activeSessionId,
            changeId,
            request.Category,
            request.TargetIdentity,
            request.OriginalValue,
            request.CurrentValue,
            recordedAtUtc,
            request.ExecutionOrder,
            OwnershipEvidenceSource.MutationLedger,
            lifecycle);

    private static RecordedMutationExecution Result(
        string operationIdentity,
        Guid changeId,
        Guid sessionId,
        RecordedMutationOutcome outcome,
        RestorationMutationResult? providerResult,
        bool plannedPersisted,
        bool appliedPersisted,
        bool requiresManualRecovery,
        string reason)
        => new(
            operationIdentity,
            changeId,
            sessionId,
            outcome,
            providerResult,
            plannedPersisted,
            appliedPersisted,
            requiresManualRecovery,
            reason);
}