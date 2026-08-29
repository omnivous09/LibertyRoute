namespace LibertyRoute.Restoration;

public enum RecordPurpose
{
    SessionMutation,
    RecoveryMutation
}

/// <summary>
/// Lifecycle of a LibertyRoute-owned change recorded in the ownership ledger.
/// The lifecycle may only advance forward: Planned to Applied to Reverted.
/// </summary>
public enum OwnedChangeLifecycle
{
    Planned = 0,
    Applied = 1,
    Reverted = 2
}

/// <summary>
/// Persistence contract for recorded LibertyRoute-owned changes.
/// The ledger stores operational metadata only and exposes no network mutation operations.
/// Ownership evidence is created exclusively by explicit recording; it is never inferred
/// from snapshots, route presence, timing, or value similarity.
/// </summary>
public interface IOwnershipLedger
{
    /// <summary>
    /// Appends one owned-change record. Appending an identical record again is an
    /// idempotent no-op. Advancing the lifecycle Planned to Applied to Reverted with
    /// identical immutable fields is permitted. Any other duplicate ChangeId collision
    /// fails closed.
    /// </summary>
    Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken);

    /// <summary>
    /// Reads all records recorded for one session. A missing ledger reads as empty.
    /// Records from any other session are never returned.
    /// </summary>
    Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Clears only the ledger of the explicitly requested session. Other sessions are untouched.
    /// </summary>
    Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether a specific change id exists for a session.
    /// </summary>
    Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken);
}

/// <summary>
/// Persisted record of a single LibertyRoute-owned change. It captures exactly the
/// metadata required to map deterministically into <see cref="OwnershipEvidence"/>.
/// It contains operational metadata only and must never contain secrets, credentials,
/// certificates, tokens, or key material.
///
/// <see cref="IsComplete"/> represents CURRENT authorization eligibility, not historical
/// evidence that the change was once applied: only a record whose current lifecycle is
/// Applied is complete. Planned and Reverted records map to incomplete evidence and can
/// never authorize restoration.
/// </summary>
public sealed record PersistedOwnedChange(
    Guid SessionId,
    Guid ChangeId,
    DryRunOperationCategory Category,
    string TargetIdentity,
    string OriginalValue,
    string AppliedValue,
    DateTimeOffset RecordedAtUtc,
    int? SequenceNumber,
    OwnershipEvidenceSource EvidenceSource,
    OwnedChangeLifecycle Lifecycle,
    bool IsComplete,
    RecordPurpose Purpose = RecordPurpose.SessionMutation,
    Guid? RecoveryAttemptId = null,
    Guid? AuthorizationEvidenceId = null)
{
    public bool IsRecoveryMutation => Purpose == RecordPurpose.RecoveryMutation;
    public bool HasRecoveryProvenance => Purpose == RecordPurpose.RecoveryMutation && RecoveryAttemptId.HasValue && AuthorizationEvidenceId.HasValue;

    public Guid DeriveOwnershipChangeId(string operationIdentity)
        => MutationOwnershipCoordinator.DeriveChangeId(SessionId, operationIdentity);

    public static PersistedOwnedChange Create(
        Guid sessionId,
        Guid changeId,
        DryRunOperationCategory category,
        string targetIdentity,
        string originalValue,
        string appliedValue,
        DateTimeOffset recordedAtUtc,
        int? sequenceNumber,
        OwnershipEvidenceSource evidenceSource,
        OwnedChangeLifecycle lifecycle,
        RecordPurpose purpose = RecordPurpose.SessionMutation,
        Guid? recoveryAttemptId = null,
        Guid? authorizationEvidenceId = null)
    {
        if (!TryCreate(
                sessionId,
                changeId,
                category,
                targetIdentity,
                originalValue,
                appliedValue,
                recordedAtUtc,
                sequenceNumber,
                evidenceSource,
                lifecycle,
                purpose,
                recoveryAttemptId,
                authorizationEvidenceId,
                out var record,
                out var failureReason))
        {
            throw new InvalidOperationException(failureReason);
        }

        return record!;
    }

    public static bool TryCreate(
        Guid sessionId,
        Guid changeId,
        DryRunOperationCategory category,
        string targetIdentity,
        string originalValue,
        string appliedValue,
        DateTimeOffset recordedAtUtc,
        int? sequenceNumber,
        OwnershipEvidenceSource evidenceSource,
        OwnedChangeLifecycle lifecycle,
        out PersistedOwnedChange? record,
        out string failureReason)
        => TryCreate(
            sessionId,
            changeId,
            category,
            targetIdentity,
            originalValue,
            appliedValue,
            recordedAtUtc,
            sequenceNumber,
            evidenceSource,
            lifecycle,
            RecordPurpose.SessionMutation,
            null,
            null,
            out record,
            out failureReason);

    public static bool TryCreate(
        Guid sessionId,
        Guid changeId,
        DryRunOperationCategory category,
        string targetIdentity,
        string originalValue,
        string appliedValue,
        DateTimeOffset recordedAtUtc,
        int? sequenceNumber,
        OwnershipEvidenceSource evidenceSource,
        OwnedChangeLifecycle lifecycle,
        RecordPurpose purpose,
        Guid? recoveryAttemptId,
        Guid? authorizationEvidenceId,
        out PersistedOwnedChange? record,
        out string failureReason)
    {
        record = null;
        failureReason = ValidateComponents(
            sessionId,
            changeId,
            category,
            targetIdentity,
            originalValue,
            appliedValue,
            recordedAtUtc,
            sequenceNumber,
            evidenceSource,
            lifecycle,
            isComplete: null,
            purpose,
            recoveryAttemptId,
            authorizationEvidenceId);
        if (!string.IsNullOrEmpty(failureReason))
            return false;

        record = new PersistedOwnedChange(
            sessionId,
            changeId,
            category,
            targetIdentity,
            originalValue,
            appliedValue,
            recordedAtUtc,
            sequenceNumber,
            evidenceSource,
            lifecycle,
            lifecycle == OwnedChangeLifecycle.Applied,
            purpose,
            recoveryAttemptId,
            authorizationEvidenceId);
        failureReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates a record instance, including records read back from storage or handed
    /// directly to a ledger. Every persistence boundary re-validates and fails closed.
    /// </summary>
    internal static string Validate(PersistedOwnedChange record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ValidateComponents(
            record.SessionId,
            record.ChangeId,
            record.Category,
            record.TargetIdentity,
            record.OriginalValue,
            record.AppliedValue,
            record.RecordedAtUtc,
            record.SequenceNumber,
            record.EvidenceSource,
            record.Lifecycle,
            record.IsComplete,
            record.Purpose,
            record.RecoveryAttemptId,
            record.AuthorizationEvidenceId);
    }

    /// <summary>
    /// Maps this record onto <see cref="OwnershipEvidence"/> with a strict one-to-one
    /// field copy. No value is guessed, inferred, or defaulted.
    /// </summary>
    public OwnershipEvidence ToOwnershipEvidence()
        => new(
            SessionId,
            Category,
            TargetIdentity,
            OriginalValue,
            AppliedValue,
            ChangeId,
            RecordedAtUtc,
            SequenceNumber,
            EvidenceSource,
            IsComplete);

    /// <summary>
    /// Reports whether the identity and evidence fields of two records with the same
    /// ChangeId match exactly. Lifecycle and IsComplete are intentionally excluded:
    /// lifecycle may legally advance forward, and IsComplete is derived from it.
    /// </summary>
    internal bool ImmutableFieldsMatch(PersistedOwnedChange other)
        => other is not null &&
           SessionId == other.SessionId &&
           ChangeId == other.ChangeId &&
           Category == other.Category &&
           StringComparer.Ordinal.Equals(TargetIdentity, other.TargetIdentity) &&
           StringComparer.Ordinal.Equals(OriginalValue, other.OriginalValue) &&
           StringComparer.Ordinal.Equals(AppliedValue, other.AppliedValue) &&
           RecordedAtUtc.Equals(other.RecordedAtUtc) &&
           Nullable.Equals(SequenceNumber, other.SequenceNumber) &&
           EvidenceSource == other.EvidenceSource;

    /// <summary>
    /// Only forward single-step lifecycle advances are valid:
    /// Planned to Applied, and Applied to Reverted.
    /// </summary>
    internal static bool IsValidTransition(OwnedChangeLifecycle current, OwnedChangeLifecycle next)
        => (current == OwnedChangeLifecycle.Planned && next == OwnedChangeLifecycle.Applied) ||
           (current == OwnedChangeLifecycle.Applied && next == OwnedChangeLifecycle.Reverted);

    private static string ValidateComponents(
        Guid sessionId,
        Guid changeId,
        DryRunOperationCategory category,
        string targetIdentity,
        string originalValue,
        string appliedValue,
        DateTimeOffset recordedAtUtc,
        int? sequenceNumber,
        OwnershipEvidenceSource evidenceSource,
        OwnedChangeLifecycle lifecycle,
        bool? isComplete,
        RecordPurpose purpose,
        Guid? recoveryAttemptId,
        Guid? authorizationEvidenceId)
    {
        if (sessionId == Guid.Empty)
            return "Session id is required.";
        if (changeId == Guid.Empty)
            return "Change id is required.";
        if (!Enum.IsDefined(category))
            return "The change category is invalid.";
        if (string.IsNullOrWhiteSpace(targetIdentity))
            return "Target identity is required.";
        if (string.IsNullOrWhiteSpace(originalValue))
            return "Original value is required.";
        if (string.IsNullOrWhiteSpace(appliedValue))
            return "Applied value is required.";
        if (recordedAtUtc == default)
            return "Recorded timestamp is required.";
        if (sequenceNumber.HasValue && sequenceNumber.Value <= 0)
            return "Sequence number must be greater than zero when present.";
        if (!Enum.IsDefined(evidenceSource))
            return "Evidence source is invalid.";
        if (!Enum.IsDefined(lifecycle))
            return "Lifecycle is invalid.";
        if (!Enum.IsDefined(purpose))
            return "Ownership record purpose is invalid.";
        if (purpose == RecordPurpose.SessionMutation)
        {
            if (recoveryAttemptId.HasValue && recoveryAttemptId.Value != Guid.Empty)
                return "Session mutation provenance must not include a recovery attempt id.";
            if (authorizationEvidenceId.HasValue && authorizationEvidenceId.Value != Guid.Empty)
                return "Session mutation provenance must not include an authorization evidence id.";
        }
        else
        {
            if (!recoveryAttemptId.HasValue || recoveryAttemptId.Value == Guid.Empty)
                return "Recovery mutation provenance requires a non-empty RecoveryAttemptId.";
            if (!authorizationEvidenceId.HasValue || authorizationEvidenceId.Value == Guid.Empty)
                return "Recovery mutation provenance requires a non-empty AuthorizationEvidenceId.";
        }
        if (isComplete.HasValue && isComplete.Value != (lifecycle == OwnedChangeLifecycle.Applied))
            return "Completeness must reflect the current lifecycle: only Applied changes are complete.";
        return string.Empty;
    }
}

/// <summary>
/// Canonical deterministic ordering for ledger records. It mirrors the ordering the
/// authorization policy applies to ownership evidence candidates.
/// </summary>
internal static class OwnershipLedgerOrdering
{
    public static PersistedOwnedChange[] Order(IEnumerable<PersistedOwnedChange> records)
        => records
            .OrderBy(record => record.SequenceNumber ?? int.MaxValue)
            .ThenBy(record => record.ChangeId)
            .ThenBy(record => record.RecordedAtUtc)
            .ToArray();
}