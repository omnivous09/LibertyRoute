using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

public enum RecoveryStartupReconciliationStatus
{
    NoJournal,
    LegacyRecoveryRequired,
    ReconciledAndCleared,
    ManualRecoveryRequired,
    ManualRecoveryPersistenceFailed,
    TerminalClearPending,
    MalformedJournal,
    MalformedLedger,
    StaleJournal,
    StaleLedger
}

public sealed record RecoveryStartupReconciliationResult(
    RecoveryStartupReconciliationStatus Status,
    Guid? SessionId,
    Guid? RecoveryAttemptId,
    RecoveryPhase? DurablePhase,
    bool CapturedNetworkState,
    string Reason);

public interface IRecoveryStartupReconciler
{
    Task<RecoveryStartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Reconciles durable recovery bookkeeping at startup. This type intentionally has no
/// mutation-provider, activation, approval, capability, or mutation-executor dependency.
/// </summary>
public sealed class RecoveryStartupReconciler : IRecoveryStartupReconciler
{
    private const int MaximumProgressSteps = 12;
    private readonly IRecoveryTransactionJournal _journal;
    private readonly IConditionalOwnershipLedger _ledger;
    private readonly INetworkStateManager _network;
    private readonly IRecoveryBaselineVerifier _baselineVerifier;

    public RecoveryStartupReconciler(
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

    public async Task<RecoveryStartupReconciliationResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        using var lease = await RecoverySessionLease.AcquireAsync(_journal, cancellationToken).ConfigureAwait(false);
        RecoveryJournalSnapshot? current;
        try
        {
            current = await _journal.ReadActiveRecoveryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            return Result(RecoveryStartupReconciliationStatus.MalformedJournal, (RecoveryJournalSnapshot?)null, false, exception.Message);
        }

        if (current is null)
            return Result(RecoveryStartupReconciliationStatus.NoJournal, (RecoveryJournalSnapshot?)null, false, "No active journal exists.");
        if (current.Transaction.RecoveryCompletion is null)
            return Result(RecoveryStartupReconciliationStatus.LegacyRecoveryRequired, current, false, "The active journal is a legacy transaction.");

        var captured = false;
        for (var step = 0; step < MaximumProgressSteps; step++)
        {
            var completion = current.Transaction.RecoveryCompletion!;
            if (completion.Phase == RecoveryPhase.ManualRecoveryRequired)
                return Result(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, current, captured, "The durable recovery already requires manual intervention.");

            OwnershipLedgerSnapshot ledger;
            try
            {
                ledger = await _ledger.ReadVersionedAsync(current.Transaction.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                if (completion.Phase == RecoveryPhase.TerminalCommitted)
                    return Result(RecoveryStartupReconciliationStatus.MalformedLedger, current, captured, exception.Message);
                return await ManualAsync(current, captured, $"Ownership ledger is malformed: {exception.Message}").ConfigureAwait(false);
            }

            var evidence = ValidateEvidence(current, ledger);
            switch (completion.Phase)
            {
                case RecoveryPhase.IntentRecorded:
                    if (!evidence.OriginalApplied || !(evidence.RecoveryAbsent || evidence.RecoveryPlanned))
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    return await ManualAsync(current, captured, "Recovery stopped before native execution.").ConfigureAwait(false);

                case RecoveryPhase.Prepared:
                    if (!evidence.OriginalApplied || !evidence.RecoveryPlanned)
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    return await ManualAsync(current, captured, "Prepared recovery was abandoned before native execution.").ConfigureAwait(false);

                case RecoveryPhase.ExecutionStarted:
                    if (!evidence.OriginalApplied || !evidence.RecoveryApplied)
                        return await ManualAsync(current, captured, "Native execution outcome is ambiguous and exact applied recovery evidence is unavailable.").ConfigureAwait(false);
                    current = await AdvanceAsync(current, RecoveryPhase.ExecutionCompleted, captured).ConfigureAwait(false);
                    if (current is null)
                        return Result(RecoveryStartupReconciliationStatus.StaleJournal, completion, captured, "ExecutionCompleted lost its exact journal expectation.");
                    continue;

                case RecoveryPhase.ExecutionCompleted:
                    if (!evidence.OriginalApplied || !evidence.RecoveryApplied)
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var fresh = await _network.CaptureStateAsync(cancellationToken).ConfigureAwait(false);
                        captured = true;
                        var verification = _baselineVerifier.Verify(current.Transaction.Snapshot, fresh, completion.Manifest!);
                        if (!verification.IsVerified)
                            return await ManualAsync(current, captured, verification.Reason).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        return await ManualAsync(current, captured, $"Fresh baseline capture or verification failed: {exception.GetType().Name}.").ConfigureAwait(false);
                    }
                    current = await AdvanceAsync(current, RecoveryPhase.BaselineVerified, captured).ConfigureAwait(false);
                    if (current is null)
                        return Result(RecoveryStartupReconciliationStatus.StaleJournal, completion, captured, "BaselineVerified lost its exact journal expectation.");
                    continue;

                case RecoveryPhase.BaselineVerified:
                    if (!evidence.OriginalApplied || !evidence.RecoveryApplied)
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    current = await AdvanceAsync(current, RecoveryPhase.LedgerFinalizing, captured).ConfigureAwait(false);
                    if (current is null)
                        return Result(RecoveryStartupReconciliationStatus.StaleJournal, completion, captured, "LedgerFinalizing lost its exact journal expectation.");
                    continue;

                case RecoveryPhase.LedgerFinalizing:
                    if (!evidence.RecoveryApplied || !(evidence.OriginalApplied || evidence.OriginalReverted))
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    if (evidence.OriginalApplied)
                    {
                        var previousLedgerRevision = ledger.LedgerRevision;
                        var transitions = ExactOriginals(current, ledger)
                            .Select(record => new OwnershipRecordTransition(record.ChangeId, OwnedChangeLifecycle.Applied,
                                record with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false }))
                            .ToArray();
                        if (transitions.Length != completion.Manifest!.OriginalEvidenceBindings.Count)
                            return await ManualAsync(current, captured, "The complete original evidence set is unavailable.").ConfigureAwait(false);
                        if (!await _ledger.TryApplyTransitionsAsync(current.Transaction.SessionId, ledger.LedgerRevision,
                                transitions, cancellationToken).ConfigureAwait(false))
                            return Result(RecoveryStartupReconciliationStatus.StaleLedger, current, captured, "Original finalization lost its exact ledger expectation.");

                        // Publication succeeded: caller cancellation cannot suppress verification.
                        try { ledger = await _ledger.ReadVersionedAsync(current.Transaction.SessionId, CancellationToken.None).ConfigureAwait(false); }
                        catch (InvalidDataException exception) { return Result(RecoveryStartupReconciliationStatus.MalformedLedger, current, captured, exception.Message); }
                        evidence = ValidateEvidence(current, ledger);
                        if (StringComparer.Ordinal.Equals(previousLedgerRevision, ledger.LedgerRevision) ||
                            !evidence.OriginalReverted || !evidence.RecoveryApplied)
                        {
                            if (StringComparer.Ordinal.Equals(previousLedgerRevision, ledger.LedgerRevision))
                                return Result(RecoveryStartupReconciliationStatus.StaleLedger, current, captured,
                                    "Published original finalization did not produce a new durable ledger revision.");
                            return await ManualAsync(current, captured, "Published original finalization could not be verified.").ConfigureAwait(false);
                        }
                    }
                    current = await AdvanceAsync(current, RecoveryPhase.LedgerFinalized, captured).ConfigureAwait(false);
                    if (current is null)
                        return Result(RecoveryStartupReconciliationStatus.StaleJournal, completion, captured, "LedgerFinalized lost its exact journal expectation.");
                    continue;

                case RecoveryPhase.LedgerFinalized:
                    if (!evidence.OriginalReverted || !evidence.RecoveryApplied)
                        return await ManualAsync(current, captured, evidence.Reason).ConfigureAwait(false);
                    current = await AdvanceAsync(current, RecoveryPhase.TerminalCommitted, captured).ConfigureAwait(false);
                    if (current is null)
                        return Result(RecoveryStartupReconciliationStatus.StaleJournal, completion, captured, "TerminalCommitted lost its exact journal expectation.");
                    continue;

                case RecoveryPhase.TerminalCommitted:
                    if (!evidence.OriginalReverted || !evidence.RecoveryApplied)
                        return Result(RecoveryStartupReconciliationStatus.MalformedLedger, current, captured, evidence.Reason);
                    var cleared = await _journal.TryClearTerminalRecoveryAsync(
                        current.Transaction.SessionId, completion.AuthorizedTransactionFingerprint,
                        completion.RecoveryAttemptId, completion.RecoveryManifestFingerprint,
                        cancellationToken).ConfigureAwait(false);
                    if (cleared)
                    {
                        // A successful delete is the publication; verify it without caller cancellation.
                        RecoveryJournalSnapshot? after;
                        try { after = await _journal.ReadActiveRecoveryAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (InvalidDataException exception) { return Result(RecoveryStartupReconciliationStatus.MalformedJournal, current, captured, exception.Message); }
                        return after is null
                            ? Result(RecoveryStartupReconciliationStatus.ReconciledAndCleared, current, captured, "Terminal recovery was exactly cleared.")
                            : Result(RecoveryStartupReconciliationStatus.TerminalClearPending, after, captured, "A journal appeared after terminal clear.");
                    }
                    return Result(RecoveryStartupReconciliationStatus.TerminalClearPending, current, captured, "Exact terminal clear did not match durable state.");
            }
        }

        return Result(RecoveryStartupReconciliationStatus.StaleJournal, current, captured, "Reconciliation exceeded its bounded progress limit.");
    }

    private async Task<RecoveryJournalSnapshot?> AdvanceAsync(
        RecoveryJournalSnapshot current,
        RecoveryPhase next,
        bool captured)
    {
        var completion = current.Transaction.RecoveryCompletion!;
        var proposed = current.Transaction with { RecoveryCompletion = completion.WithPhase(next) };
        var expected = Expectation(current);
        if (!await _journal.TryAdvanceRecoveryAsync(expected, proposed, CancellationToken.None).ConfigureAwait(false))
            return null;
        var reread = await _journal.ReadActiveRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
        return ExactPublished(current, proposed, next, reread) ? reread : null;
    }

    private async Task<RecoveryStartupReconciliationResult> ManualAsync(
        RecoveryJournalSnapshot current,
        bool captured,
        string reason)
    {
        try
        {
            var completion = current.Transaction.RecoveryCompletion!;
            var proposed = current.Transaction with
            {
                RecoveryCompletion = completion.WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: reason)
            };
            if (!await _journal.TryAdvanceRecoveryAsync(Expectation(current), proposed, CancellationToken.None).ConfigureAwait(false))
                return Result(RecoveryStartupReconciliationStatus.ManualRecoveryPersistenceFailed, current, captured, reason);
            var reread = await _journal.ReadActiveRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
            return ExactPublished(current, proposed, RecoveryPhase.ManualRecoveryRequired, reread)
                ? Result(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, reread!, captured, reason)
                : Result(RecoveryStartupReconciliationStatus.ManualRecoveryPersistenceFailed, reread ?? current, captured, reason);
        }
        catch
        {
            return Result(RecoveryStartupReconciliationStatus.ManualRecoveryPersistenceFailed, current, captured, reason);
        }
    }

    private static RecoveryTransitionExpectation Expectation(RecoveryJournalSnapshot current)
    {
        var completion = current.Transaction.RecoveryCompletion!;
        return new(current.Transaction.SessionId, current.JournalRevision, completion.Phase,
            completion.RecoveryAttemptId, completion.AuthorizedTransactionFingerprint,
            completion.RecoveryManifestFingerprint);
    }

    private static bool ExactPublished(RecoveryJournalSnapshot previous, NetworkTransaction proposed,
        RecoveryPhase phase, RecoveryJournalSnapshot? reread)
        => reread is not null &&
           !StringComparer.Ordinal.Equals(previous.JournalRevision, reread.JournalRevision) &&
           reread.Transaction.RecoveryCompletion?.Phase == phase &&
           string.IsNullOrEmpty(RecoveryCompletion.ValidateImmutableIdentity(proposed, reread.Transaction));

    private static EvidenceState ValidateEvidence(RecoveryJournalSnapshot journal, OwnershipLedgerSnapshot ledger)
    {
        var manifest = journal.Transaction.RecoveryCompletion!.Manifest!;
        var originals = ExactOriginals(journal, ledger).ToArray();
        var originalApplied = originals.Length == manifest.OriginalEvidenceBindings.Count &&
                              originals.All(record => record.Lifecycle == OwnedChangeLifecycle.Applied);
        var originalReverted = originals.Length == manifest.OriginalEvidenceBindings.Count &&
                               originals.All(record => record.Lifecycle == OwnedChangeLifecycle.Reverted);
        var recovery = ledger.Records.SingleOrDefault(record => record.ChangeId == manifest.RecoveryOwnershipChangeId);
        var recoveryExact = recovery is not null &&
            recovery.SessionId == manifest.SessionId &&
            recovery.ChangeId == OwnershipIdentity.DeriveChangeId(manifest.SessionId, manifest.OperationIdentity) &&
            StringComparer.Ordinal.Equals(recovery.Category.ToString(), manifest.OperationCategory) &&
            StringComparer.Ordinal.Equals(recovery.TargetIdentity, manifest.TargetIdentity) &&
            StringComparer.Ordinal.Equals(recovery.OriginalValue, manifest.OriginalValue) &&
            StringComparer.Ordinal.Equals(recovery.AppliedValue, manifest.AppliedValue) &&
            recovery.SequenceNumber == manifest.SequenceOrder &&
            recovery.EvidenceSource == OwnershipEvidenceSource.MutationLedger &&
            recovery.Purpose == RecordPurpose.RecoveryMutation &&
            recovery.RecoveryAttemptId == manifest.RecoveryAttemptId &&
            recovery.AuthorizationEvidenceId == manifest.OriginalEvidenceBindings.Single().EvidenceId;
        return new(originalApplied, originalReverted, recovery is null,
            recoveryExact && recovery!.Lifecycle == OwnedChangeLifecycle.Planned,
            recoveryExact && recovery!.Lifecycle == OwnedChangeLifecycle.Applied,
            "Ownership evidence does not exactly match the durable recovery manifest.");
    }

    private static IEnumerable<PersistedOwnedChange> ExactOriginals(
        RecoveryJournalSnapshot journal,
        OwnershipLedgerSnapshot ledger)
    {
        var bindings = journal.Transaction.RecoveryCompletion!.Manifest!.OriginalEvidenceBindings;
        foreach (var binding in bindings)
        {
            var record = ledger.Records.SingleOrDefault(candidate => candidate.ChangeId == binding.EvidenceId);
            if (record is null || record.Purpose != RecordPurpose.SessionMutation ||
                record.SessionId != journal.Transaction.SessionId)
                continue;
            var actual = RecoveryOwnershipEvidenceBinding.Create(record);
            if (actual.EvidenceId == binding.EvidenceId &&
                StringComparer.Ordinal.Equals(actual.EvidenceIdentity, binding.EvidenceIdentity) &&
                StringComparer.Ordinal.Equals(actual.EvidenceFingerprint, binding.EvidenceFingerprint))
                yield return record;
        }
    }

    private sealed record EvidenceState(bool OriginalApplied, bool OriginalReverted,
        bool RecoveryAbsent, bool RecoveryPlanned, bool RecoveryApplied, string Reason);

    private static RecoveryStartupReconciliationResult Result(
        RecoveryStartupReconciliationStatus status,
        RecoveryJournalSnapshot? snapshot,
        bool captured,
        string reason)
        => new(status, snapshot?.Transaction.SessionId,
            snapshot?.Transaction.RecoveryCompletion?.RecoveryAttemptId,
            snapshot?.Transaction.RecoveryCompletion?.Phase, captured, reason);

    private static RecoveryStartupReconciliationResult Result(
        RecoveryStartupReconciliationStatus status,
        RecoveryCompletion completion,
        bool captured,
        string reason)
        => new(status, completion.Manifest?.SessionId, completion.RecoveryAttemptId,
            completion.Phase, captured, reason);
}
