namespace LibertyRoute.Restoration;

public enum OwnershipEvidenceSource
{
    TransactionJournal,
    MutationLedger,
    TestFixture
}

public sealed record OwnershipEvidence(
    Guid SessionId,
    DryRunOperationCategory Category,
    string TargetIdentity,
    string OriginalValue,
    string AppliedValue,
    Guid ChangeId,
    DateTimeOffset CreatedAtUtc,
    int? SequenceNumber,
    OwnershipEvidenceSource EvidenceSource,
    bool IsComplete);

public enum OperationAuthorizationStatus
{
    Authorized,
    DeniedNoOwnership,
    DeniedOwnershipMismatch,
    DeniedUnsupported,
    DeniedUnverifiable,
    ManualReviewRequired
}

public sealed record OperationAuthorization(
    string OperationIdentity,
    DryRunRestorationOperation Operation,
    OperationAuthorizationStatus Status,
    string Reason,
    OwnershipEvidence? MatchedEvidence,
    bool FutureAutomaticExecutionAllowed);

public sealed record BatchAuthorizationResult(
    IReadOnlyList<OperationAuthorization> Decisions,
    IReadOnlyList<DryRunRestorationOperation> AuthorizedOperations,
    IReadOnlyList<DryRunRestorationOperation> DeniedOperations,
    IReadOnlyList<DryRunRestorationOperation> ManualReviewOperations,
    IReadOnlyList<string> BlockingReasons,
    bool FutureAutomaticExecutionAllowed);

public static class RestorationAuthorizationPolicy
{
    public static OperationAuthorization Authorize(
        DryRunRestorationOperation operation,
        IEnumerable<OwnershipEvidence> ownershipEvidence,
        Guid activeSessionId)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(ownershipEvidence);

        var operationIdentity = GetOperationIdentity(operation);
        if (operation.SafetyState == DryRunSafetyState.Unsupported)
            return Decision(operation, operationIdentity, OperationAuthorizationStatus.DeniedUnsupported, "The operation category is unsupported by the read-only authorization layer.", null);
        if (operation.SafetyState == DryRunSafetyState.Unverifiable)
            return Decision(operation, operationIdentity, OperationAuthorizationStatus.DeniedUnverifiable, "The operation cannot be verified from the available state.", null);

        var candidates = ownershipEvidence
            .Where(evidence => evidence.Category == operation.Category &&
                               StringComparer.Ordinal.Equals(evidence.TargetIdentity, operation.TargetIdentity))
            .OrderBy(evidence => evidence.SequenceNumber ?? int.MaxValue)
            .ThenBy(evidence => evidence.ChangeId)
            .ToArray();

        var sessionCandidates = candidates
            .Where(evidence => evidence.SessionId == activeSessionId)
            .ToArray();
        var mismatchedSessionCandidate = candidates
            .FirstOrDefault(evidence => evidence.SessionId != activeSessionId);

        if (sessionCandidates.Length == 0)
        {
            return Decision(
                operation,
                operationIdentity,
                OperationAuthorizationStatus.DeniedNoOwnership,
                "No ownership evidence matches the active session, category, and target.",
                mismatchedSessionCandidate);
        }

        var evidence = sessionCandidates.FirstOrDefault(item => item.IsComplete &&
            StringComparer.Ordinal.Equals(item.OriginalValue, ExpectedOriginal(operation)) &&
            StringComparer.Ordinal.Equals(item.AppliedValue, ExpectedApplied(operation)));
        if (evidence is null)
        {
            return Decision(operation, operationIdentity, OperationAuthorizationStatus.DeniedOwnershipMismatch,
                "Ownership evidence exists in the active session, but target, original value, or LibertyRoute-applied value does not exactly match.",
                sessionCandidates.FirstOrDefault());
        }

        if (operation.Action == DryRunAction.ManualReview)
        {
            return Decision(operation, operationIdentity, OperationAuthorizationStatus.Authorized,
                "Complete ownership evidence authorizes the future removal of this LibertyRoute-owned addition; no removal is performed by this policy.", evidence);
        }

        return Decision(operation, operationIdentity, OperationAuthorizationStatus.Authorized,
            "Complete ownership evidence authorizes future restoration; no restoration is performed by this policy.", evidence);
    }

    public static BatchAuthorizationResult AuthorizeBatch(
        DryRunRestorationResult dryRunResult,
        IEnumerable<OwnershipEvidence> ownershipEvidence,
        Guid activeSessionId)
    {
        ArgumentNullException.ThrowIfNull(dryRunResult);
        ArgumentNullException.ThrowIfNull(ownershipEvidence);

        var evidence = ownershipEvidence.ToArray();
        var decisions = dryRunResult.Operations
            .Select(operation => Authorize(operation, evidence, activeSessionId))
            .OrderBy(decision => decision.Operation.ExecutionOrder)
            .ThenBy(decision => decision.OperationIdentity, StringComparer.Ordinal)
            .ToArray();
        var authorized = decisions.Where(decision => decision.Status == OperationAuthorizationStatus.Authorized).Select(decision => decision.Operation).ToArray();
        var manualReview = decisions.Where(decision => decision.Status == OperationAuthorizationStatus.ManualReviewRequired).Select(decision => decision.Operation).ToArray();
        var denied = decisions.Where(decision => decision.Status is not OperationAuthorizationStatus.Authorized and not OperationAuthorizationStatus.ManualReviewRequired).Select(decision => decision.Operation).ToArray();
        var blockers = decisions.Where(decision => decision.Status != OperationAuthorizationStatus.Authorized)
            .Select(decision => decision.Reason)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();

        return new BatchAuthorizationResult(
            decisions,
            authorized,
            denied,
            manualReview,
            blockers,
            decisions.Length > 0 && decisions.All(decision => decision.Status == OperationAuthorizationStatus.Authorized && decision.FutureAutomaticExecutionAllowed));
    }

    public static string GetOperationIdentity(DryRunRestorationOperation operation)
        => $"{operation.Category}|{operation.Action}|{operation.TargetIdentity}|{operation.OriginalValue}|{operation.CurrentValue}";

    private static string ExpectedOriginal(DryRunRestorationOperation operation)
        => operation.Action == DryRunAction.ManualReview ? "<absent>" : operation.OriginalValue;

    private static string ExpectedApplied(DryRunRestorationOperation operation)
        => operation.Action == DryRunAction.ManualReview ? operation.CurrentValue : operation.CurrentValue;

    private static OperationAuthorization Decision(
        DryRunRestorationOperation operation,
        string operationIdentity,
        OperationAuthorizationStatus status,
        string reason,
        OwnershipEvidence? evidence)
        => new(operationIdentity, operation, status, reason, evidence, status == OperationAuthorizationStatus.Authorized);
}
