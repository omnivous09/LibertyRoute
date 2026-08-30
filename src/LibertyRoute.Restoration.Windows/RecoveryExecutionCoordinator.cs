using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

internal enum RecoveryExecutionStatus
{
    Completed,
    RejectedBeforeIntent,
    StaleJournal,
    StaleLedger,
    ManualRecoveryRequired,
    ManualRecoveryPersistenceFailed,
    TerminalClearPending
}

internal sealed record RecoveryExecutionResult(
    RecoveryExecutionStatus Status,
    Guid? RecoveryAttemptId,
    RecoveryPhase? DurablePhase,
    bool ProviderInvoked,
    RestorationMutationResult? ProviderResult,
    string Reason);

internal sealed class RecoveryExecutionCoordinator
{
    private readonly IRecoveryTransactionJournal _journal;
    private readonly IConditionalOwnershipLedger _ledger;
    private readonly INetworkStateManager _network;
    private readonly IRecoveryBaselineVerifier _baselineVerifier;

    internal RecoveryExecutionCoordinator(
        IRecoveryTransactionJournal journal,
        IConditionalOwnershipLedger ledger,
        INetworkStateManager network,
        IRecoveryBaselineVerifier baselineVerifier)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _baselineVerifier = baselineVerifier ?? throw new ArgumentNullException(nameof(baselineVerifier));
    }

    internal async Task<RecoveryExecutionResult> ExecuteAsync(
        RecoveryJournalSnapshot authorizedJournal,
        RestorationOrchestrationPreparation preparation,
        AuthorizedRestorationRequest operation,
        string preparationFingerprint,
        IRestorationMutationProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorizedJournal);
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationFingerprint);
        ArgumentNullException.ThrowIfNull(provider);

        if (operation.Category != DryRunOperationCategory.Route ||
            preparation.ActiveSessionId != authorizedJournal.Transaction.SessionId ||
            operation.SessionId != authorizedJournal.Transaction.SessionId ||
            preparation.ExecutionPreparation.AuthorizedRequests.Count != 1 ||
            !ReferenceEquals(preparation.ExecutionPreparation.AuthorizedRequests[0], operation))
            return Result(RecoveryExecutionStatus.RejectedBeforeIntent, null, null, false, null, "The prepared recovery operation is unsupported or does not match the exact session.");

        cancellationToken.ThrowIfCancellationRequested();
        var ledger = await _ledger.ReadVersionedAsync(operation.SessionId, cancellationToken).ConfigureAwait(false);
        var original = ledger.Records.SingleOrDefault(record => record.ChangeId == operation.AuthorizationEvidenceId);
        if (original is null || original.Lifecycle != OwnedChangeLifecycle.Applied ||
            original.Purpose != RecordPurpose.SessionMutation || original.SessionId != operation.SessionId ||
            original.Category != operation.Category ||
            !StringComparer.Ordinal.Equals(original.TargetIdentity, operation.TargetIdentity) ||
            !StringComparer.Ordinal.Equals(original.OriginalValue, operation.OriginalValue) ||
            !StringComparer.Ordinal.Equals(original.AppliedValue, operation.CurrentValue))
            return Result(RecoveryExecutionStatus.RejectedBeforeIntent, null, null, false, null, "The exact applied authorization evidence is unavailable.");

        var attemptId = Guid.NewGuid();
        var recoveryChangeId = OwnershipIdentity.DeriveChangeId(operation.SessionId, operation.OperationIdentity);
        if (recoveryChangeId == original.ChangeId)
            return Result(RecoveryExecutionStatus.RejectedBeforeIntent, attemptId, null, false, null, "Recovery ownership identity collides with its authorization evidence.");

        var authorizedFingerprint = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(authorizedJournal.Transaction);
        var binding = RecoveryOwnershipEvidenceBinding.Create(original);
        var manifest = RecoveryManifest.Create(
            attemptId, operation.SessionId, authorizedJournal.Transaction.OwnerSid, authorizedFingerprint,
            new[] { binding }, recoveryChangeId, operation.OperationIdentity, operation.Category.ToString(),
            operation.TargetIdentity, operation.IntendedRestorationValue, operation.CurrentValue,
            operation.ExecutionOrder, preparationFingerprint);
        var manifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest);
        var completion = new RecoveryCompletion(
            attemptId, RecoveryPhase.IntentRecorded, authorizedFingerprint, manifestFingerprint,
            RecoveryManifest.FormatCanonicalEvidenceBindings(new[] { binding }), preparationFingerprint,
            DateTimeOffset.UtcNow) { Manifest = manifest };

        var current = authorizedJournal;
        if (!await AdvanceAsync(current, null, authorizedJournal.Transaction with { RecoveryCompletion = completion }, cancellationToken).ConfigureAwait(false))
            return Result(RecoveryExecutionStatus.StaleJournal, attemptId, null, false, null, "The recovery intent lost its exact journal expectation.");
        var intentPublished = authorizedJournal.Transaction with { RecoveryCompletion = completion };
        var intentReread = await _journal.ReadActiveRecoveryAsync(cancellationToken).ConfigureAwait(false);
        if (!ExactPublishedJournal(authorizedJournal, intentPublished, RecoveryPhase.IntentRecorded, intentReread))
            return Result(RecoveryExecutionStatus.StaleJournal, attemptId, RecoveryPhase.IntentRecorded, false, null,
                "The recovery intent publication could not be verified against its exact journal identity.");
        current = intentReread!;

        var recovery = PersistedOwnedChange.Create(operation.SessionId, recoveryChangeId, operation.Category,
            operation.TargetIdentity, operation.CurrentValue, operation.IntendedRestorationValue,
            DateTimeOffset.UtcNow, operation.ExecutionOrder, OwnershipEvidenceSource.MutationLedger,
            OwnedChangeLifecycle.Planned, RecordPurpose.RecoveryMutation, attemptId, original.ChangeId);
        if (!await _ledger.TryApplyTransitionsAsync(operation.SessionId, ledger.LedgerRevision,
            new[] { new OwnershipRecordTransition(recoveryChangeId, null, recovery) }, cancellationToken).ConfigureAwait(false))
            return await ManualAsync(current, false, null, "The recovery ownership plan lost its ledger expectation.").ConfigureAwait(false);
        var plannedRevision = ledger.LedgerRevision;
        ledger = await _ledger.ReadVersionedAsync(operation.SessionId, cancellationToken).ConfigureAwait(false);
        if (StringComparer.Ordinal.Equals(plannedRevision, ledger.LedgerRevision) || !ExactRecord(ledger, recovery, OwnedChangeLifecycle.Planned))
            return await ManualAsync(current, false, null, "The durable recovery ownership plan could not be verified.").ConfigureAwait(false);

        var advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.Prepared, cancellationToken).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, false, null, "The Prepared journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch (OperationCanceledException)
        {
            await ManualAsync(current, false, null, "Recovery was cancelled before native execution.").ConfigureAwait(false);
            throw;
        }
        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.ExecutionStarted, cancellationToken).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, false, null, "The ExecutionStarted journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;

        RestorationMutationResult providerResult;
        try
        {
            providerResult = await provider.ApplyAsync(operation, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return await ManualAsync(current, true, null, $"The recovery provider outcome is ambiguous: {exception.GetType().Name}.").ConfigureAwait(false);
        }
        if (providerResult.State != RestorationMutationState.Succeeded ||
            !StringComparer.Ordinal.Equals(providerResult.OperationIdentity, operation.OperationIdentity))
            return await ManualAsync(current, true, providerResult, "The recovery provider did not report success.").ConfigureAwait(false);

        var appliedRecovery = recovery with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true };
        if (!await _ledger.TryApplyTransitionsAsync(operation.SessionId, ledger.LedgerRevision,
            new[] { new OwnershipRecordTransition(recoveryChangeId, OwnedChangeLifecycle.Planned, appliedRecovery) }, CancellationToken.None).ConfigureAwait(false))
            return await ManualAsync(current, true, providerResult, "The applied recovery ownership record could not be published.").ConfigureAwait(false);
        var appliedRevision = ledger.LedgerRevision;
        ledger = await _ledger.ReadVersionedAsync(operation.SessionId, CancellationToken.None).ConfigureAwait(false);
        if (StringComparer.Ordinal.Equals(appliedRevision, ledger.LedgerRevision) || !ExactRecord(ledger, appliedRecovery, OwnedChangeLifecycle.Applied))
            return await ManualAsync(current, true, providerResult, "The applied recovery ownership record could not be verified.").ConfigureAwait(false);

        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.ExecutionCompleted, CancellationToken.None).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, true, providerResult, "The ExecutionCompleted journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        var fresh = await _network.CaptureStateAsync(CancellationToken.None).ConfigureAwait(false);
        var verification = _baselineVerifier.Verify(authorizedJournal.Transaction.Snapshot, fresh, manifest);
        if (!verification.IsVerified)
            return await ManualAsync(current, true, providerResult, verification.Reason).ConfigureAwait(false);

        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.BaselineVerified, CancellationToken.None).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, true, providerResult, "The BaselineVerified journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.LedgerFinalizing, CancellationToken.None).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, true, providerResult, "The LedgerFinalizing journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        var revertedOriginal = original with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false };
        if (!await _ledger.TryApplyTransitionsAsync(operation.SessionId, ledger.LedgerRevision,
            new[] { new OwnershipRecordTransition(original.ChangeId, OwnedChangeLifecycle.Applied, revertedOriginal) }, CancellationToken.None).ConfigureAwait(false))
            return await ManualAsync(current, true, providerResult, "Original ownership finalization lost its ledger expectation.").ConfigureAwait(false);
        var finalLedger = await _ledger.ReadVersionedAsync(operation.SessionId, CancellationToken.None).ConfigureAwait(false);
        if (StringComparer.Ordinal.Equals(finalLedger.LedgerRevision, ledger.LedgerRevision) ||
            !ExactRecord(finalLedger, revertedOriginal, OwnedChangeLifecycle.Reverted) ||
            !ExactRecord(finalLedger, appliedRecovery, OwnedChangeLifecycle.Applied))
            return await ManualAsync(current, true, providerResult, "The final ownership ledger state could not be verified.").ConfigureAwait(false);

        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.LedgerFinalized, CancellationToken.None).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, true, providerResult, "The LedgerFinalized journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        advanced = await TryAdvancePhaseAsync(current, RecoveryPhase.TerminalCommitted, CancellationToken.None).ConfigureAwait(false);
        if (advanced is null)
            return await ManualAsync(current, true, providerResult, "The TerminalCommitted journal transition lost its exact expectation.").ConfigureAwait(false);
        current = advanced;
        var cleared = await _journal.TryClearTerminalRecoveryAsync(operation.SessionId, authorizedFingerprint,
            attemptId, manifestFingerprint, CancellationToken.None).ConfigureAwait(false);
        return cleared
            ? Result(RecoveryExecutionStatus.Completed, attemptId, RecoveryPhase.TerminalCommitted, true, providerResult, "Recovery completed and its terminal journal was cleared.")
            : Result(RecoveryExecutionStatus.TerminalClearPending, attemptId, RecoveryPhase.TerminalCommitted, true, providerResult, "Terminal recovery remains durably available for exact reconciliation.");
    }

    private async Task<RecoveryJournalSnapshot?> TryAdvancePhaseAsync(RecoveryJournalSnapshot current, RecoveryPhase phase, CancellationToken token)
    {
        var proposed = current.Transaction with { RecoveryCompletion = current.Transaction.RecoveryCompletion!.WithPhase(phase) };
        if (!await AdvanceAsync(current, current.Transaction.RecoveryCompletion!.Phase, proposed, token).ConfigureAwait(false))
            return null;
        var reread = await _journal.ReadActiveRecoveryAsync(token).ConfigureAwait(false);
        return ExactPublishedJournal(current, proposed, phase, reread) ? reread : null;
    }

    private Task<bool> AdvanceAsync(RecoveryJournalSnapshot current, RecoveryPhase? phase, NetworkTransaction proposed, CancellationToken token)
    {
        var completion = current.Transaction.RecoveryCompletion;
        return _journal.TryAdvanceRecoveryAsync(new RecoveryTransitionExpectation(
            current.Transaction.SessionId, current.JournalRevision, phase,
            completion?.RecoveryAttemptId, RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(current.Transaction),
            completion?.RecoveryManifestFingerprint), proposed, token);
    }

    private async Task<RecoveryExecutionResult> ManualAsync(RecoveryJournalSnapshot current, bool invoked, RestorationMutationResult? providerResult, string reason)
    {
        try
        {
            var completion = current.Transaction.RecoveryCompletion!;
            var proposed = current.Transaction with { RecoveryCompletion = completion.WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: reason) };
            var persisted = await AdvanceAsync(current, completion.Phase, proposed, CancellationToken.None).ConfigureAwait(false);
            if (!persisted)
                return Result(RecoveryExecutionStatus.ManualRecoveryPersistenceFailed,
                    completion.RecoveryAttemptId, completion.Phase, invoked, providerResult, reason);
            var reread = await _journal.ReadActiveRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
            var verified = ExactPublishedJournal(current, proposed, RecoveryPhase.ManualRecoveryRequired, reread);
            return Result(verified ? RecoveryExecutionStatus.ManualRecoveryRequired : RecoveryExecutionStatus.ManualRecoveryPersistenceFailed,
                completion.RecoveryAttemptId, verified ? RecoveryPhase.ManualRecoveryRequired : completion.Phase, invoked, providerResult, reason);
        }
        catch
        {
            return Result(RecoveryExecutionStatus.ManualRecoveryPersistenceFailed, current.Transaction.RecoveryCompletion?.RecoveryAttemptId,
                current.Transaction.RecoveryCompletion?.Phase, invoked, providerResult, reason);
        }
    }

    private static bool ExactRecord(OwnershipLedgerSnapshot snapshot, PersistedOwnedChange expected, OwnedChangeLifecycle lifecycle)
    {
        var record = snapshot.Records.SingleOrDefault(candidate => candidate.ChangeId == expected.ChangeId);
        return record is not null && record.Lifecycle == lifecycle && record.IsComplete == (lifecycle == OwnedChangeLifecycle.Applied) &&
            record.SessionId == expected.SessionId && record.ChangeId == expected.ChangeId && record.Category == expected.Category &&
            StringComparer.Ordinal.Equals(record.TargetIdentity, expected.TargetIdentity) &&
            StringComparer.Ordinal.Equals(record.OriginalValue, expected.OriginalValue) &&
            StringComparer.Ordinal.Equals(record.AppliedValue, expected.AppliedValue) &&
            record.RecordedAtUtc.Equals(expected.RecordedAtUtc) && record.SequenceNumber == expected.SequenceNumber &&
            record.EvidenceSource == expected.EvidenceSource && record.Purpose == expected.Purpose &&
            record.RecoveryAttemptId == expected.RecoveryAttemptId && record.AuthorizationEvidenceId == expected.AuthorizationEvidenceId;
    }

    private static bool ExactPublishedJournal(
        RecoveryJournalSnapshot previous,
        NetworkTransaction proposed,
        RecoveryPhase phase,
        RecoveryJournalSnapshot? reread)
        => reread is not null &&
           !StringComparer.Ordinal.Equals(previous.JournalRevision, reread.JournalRevision) &&
           reread.Transaction.RecoveryCompletion?.Phase == phase &&
           string.IsNullOrEmpty(RecoveryCompletion.ValidateImmutableIdentity(proposed, reread.Transaction));

    private static RecoveryExecutionResult Result(RecoveryExecutionStatus status, Guid? attempt, RecoveryPhase? phase,
        bool invoked, RestorationMutationResult? providerResult, string reason)
        => new(status, attempt, phase, invoked, providerResult, reason);
}
