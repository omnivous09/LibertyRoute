namespace LibertyRoute.Restoration;

public enum RestorationMutationState
{
    Succeeded,
    Failed,
    Skipped,
    AlreadyRestored,
    StateChangedExternally,
    Unsupported
}

public sealed record RestorationMutationResult(
    string OperationIdentity,
    RestorationMutationState State,
    string Reason,
    bool Retryable);

public interface IRestorationMutationProvider
{
    Task<RestorationMutationResult> ApplyAsync(
        AuthorizedRestorationRequest request,
        CancellationToken cancellationToken);
}

public sealed record AuthorizedRestorationRequest
{
    public Guid TransactionId { get; }
    public Guid SessionId { get; }
    public string OperationIdentity { get; }
    public DryRunOperationCategory Category { get; }
    public DryRunAction Action { get; }
    public string TargetIdentity { get; }
    public string OriginalValue { get; }
    public string CurrentValue { get; }
    public string IntendedRestorationValue { get; }
    public int ExecutionOrder { get; }
    public Guid AuthorizationEvidenceId { get; }
    public string AuthorizationReason { get; }
    public bool AutomaticallyExecutable { get; }

    private AuthorizedRestorationRequest(
        Guid transactionId,
        Guid sessionId,
        string operationIdentity,
        DryRunOperationCategory category,
        DryRunAction action,
        string targetIdentity,
        string originalValue,
        string currentValue,
        string intendedRestorationValue,
        int executionOrder,
        Guid authorizationEvidenceId,
        string authorizationReason,
        bool automaticallyExecutable)
    {
        TransactionId = transactionId;
        SessionId = sessionId;
        OperationIdentity = operationIdentity;
        Category = category;
        Action = action;
        TargetIdentity = targetIdentity;
        OriginalValue = originalValue;
        CurrentValue = currentValue;
        IntendedRestorationValue = intendedRestorationValue;
        ExecutionOrder = executionOrder;
        AuthorizationEvidenceId = authorizationEvidenceId;
        AuthorizationReason = authorizationReason;
        AutomaticallyExecutable = automaticallyExecutable;
    }

    public static AuthorizedRestorationRequest Create(
        DryRunRestorationOperation operation,
        OperationAuthorization authorization,
        Guid transactionId,
        Guid activeSessionId)
    {
        if (!TryCreate(operation, authorization, transactionId, activeSessionId, out var request, out var reason))
            throw new InvalidOperationException(reason);

        return request!;
    }

    public static bool TryCreate(
        DryRunRestorationOperation operation,
        OperationAuthorization authorization,
        Guid transactionId,
        Guid activeSessionId,
        out AuthorizedRestorationRequest? request,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(authorization);

        request = null;
        failureReason = string.Empty;

        if (authorization.Status != OperationAuthorizationStatus.Authorized)
        {
            if (authorization.MatchedEvidence is not null && authorization.MatchedEvidence.SessionId != activeSessionId)
            {
                failureReason = "Authorization session does not match the active session.";
                return false;
            }

            failureReason = authorization.Status switch
            {
                OperationAuthorizationStatus.DeniedUnsupported => "Unsupported operations are not executable.",
                OperationAuthorizationStatus.DeniedUnverifiable => "Unverifiable operations are not executable.",
                OperationAuthorizationStatus.ManualReviewRequired => "Manual review operations are not executable.",
                OperationAuthorizationStatus.DeniedNoOwnership => "No ownership evidence matches the active session, category, and target.",
                OperationAuthorizationStatus.DeniedOwnershipMismatch => "Authorization evidence does not match the operation values.",
                _ => string.IsNullOrWhiteSpace(authorization.Reason) ? "Authorization status is not Authorized." : authorization.Reason
            };
            return false;
        }

        if (!authorization.FutureAutomaticExecutionAllowed)
        {
            failureReason = "Authorization does not permit automatic execution.";
            return false;
        }

        if (authorization.MatchedEvidence is null)
        {
            failureReason = "Authorization does not contain required ownership evidence.";
            return false;
        }

        if (authorization.MatchedEvidence.SessionId != activeSessionId)
        {
            failureReason = "Authorization session does not match the active session.";
            return false;
        }

        if (operation.Action == DryRunAction.ManualReview || operation.SafetyState == DryRunSafetyState.ManualReview)
        {
            failureReason = $"Manual review operations are not executable. Original value '{operation.OriginalValue}' must match the authorized evidence and cannot be executed automatically.";
            return false;
        }

        if (operation.SafetyState is DryRunSafetyState.Unsupported)
        {
            failureReason = "Unsupported operations are not executable.";
            return false;
        }

        if (operation.SafetyState is DryRunSafetyState.Unverifiable)
        {
            failureReason = "Unverifiable operations are not executable.";
            return false;
        }

        var operationIdentity = RestorationAuthorizationPolicy.GetOperationIdentity(operation);
        if (string.IsNullOrWhiteSpace(operationIdentity))
        {
            failureReason = "Operation identity is missing or invalid.";
            return false;
        }

        if (!string.Equals(authorization.MatchedEvidence.TargetIdentity, operation.TargetIdentity, StringComparison.Ordinal))
        {
            failureReason = "Authorization target identity does not match the operation target.";
            return false;
        }

        if (!string.Equals(authorization.OperationIdentity, operationIdentity, StringComparison.Ordinal))
        {
            failureReason = "Authorization is bound to a different operation identity.";
            return false;
        }

        if (!Equals(authorization.Operation, operation))
        {
            failureReason = "Authorization is bound to a different operation instance.";
            return false;
        }

        if (authorization.MatchedEvidence.SessionId != activeSessionId)
        {
            failureReason = "Authorization session does not match the active session.";
            return false;
        }

        if (transactionId == Guid.Empty)
        {
            failureReason = "Transaction id is required for execution requests.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(operation.TargetIdentity) ||
            string.IsNullOrWhiteSpace(operation.OriginalValue) ||
            string.IsNullOrWhiteSpace(operation.CurrentValue))
        {
            failureReason = "Required operation values are missing.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(authorization.MatchedEvidence.TargetIdentity) ||
            string.IsNullOrWhiteSpace(authorization.MatchedEvidence.OriginalValue) ||
            string.IsNullOrWhiteSpace(authorization.MatchedEvidence.AppliedValue))
        {
            failureReason = "Required authorization evidence values are missing.";
            return false;
        }

        if (!Equals(authorization.MatchedEvidence.Category, operation.Category))
        {
            failureReason = "Authorization category does not match the operation category.";
            return false;
        }

        if (!string.Equals(authorization.MatchedEvidence.OriginalValue, operation.OriginalValue, StringComparison.Ordinal))
        {
            failureReason = $"Authorization original value does not match the operation original value: '{authorization.MatchedEvidence.OriginalValue}' vs '{operation.OriginalValue}'.";
            return false;
        }

        if (!string.Equals(authorization.MatchedEvidence.AppliedValue, operation.CurrentValue, StringComparison.Ordinal))
        {
            failureReason = "Authorization applied value does not match the operation current value.";
            return false;
        }

        var intendedRestorationValue = operation.OriginalValue;
        if (string.IsNullOrWhiteSpace(intendedRestorationValue))
        {
            failureReason = "Required intended restoration value is missing.";
            return false;
        }

        if (operation.ExecutionOrder <= 0)
        {
            failureReason = "Execution order must be greater than zero.";
            return false;
        }

        request = new AuthorizedRestorationRequest(
            transactionId,
            activeSessionId,
            operationIdentity,
            operation.Category,
            operation.Action,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            intendedRestorationValue,
            operation.ExecutionOrder,
            authorization.MatchedEvidence.ChangeId,
            authorization.Reason,
            authorization.FutureAutomaticExecutionAllowed);

        failureReason = string.Empty;
        return true;
    }
}

public sealed class RestorationExecutionPreparation
{
    public IReadOnlyList<AuthorizedRestorationRequest> AuthorizedRequests { get; }
    public IReadOnlyList<DryRunRestorationOperation> RejectedOperations { get; }
    public IReadOnlyList<string> BlockingReasons { get; }
    public bool CanExecuteAutomatically { get; }

    private RestorationExecutionPreparation(
        IReadOnlyList<AuthorizedRestorationRequest> authorizedRequests,
        IReadOnlyList<DryRunRestorationOperation> rejectedOperations,
        IReadOnlyList<string> blockingReasons,
        bool canExecuteAutomatically)
    {
        AuthorizedRequests = authorizedRequests;
        RejectedOperations = rejectedOperations;
        BlockingReasons = blockingReasons;
        CanExecuteAutomatically = canExecuteAutomatically;
    }

    public static RestorationExecutionPreparation Prepare(
        BatchAuthorizationResult batch,
        Guid activeTransactionId,
        Guid activeSessionId)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var requested = new List<AuthorizedRestorationRequest>();
        var rejected = new List<DryRunRestorationOperation>();
        var blockers = new List<string>();
        var executionOrders = new HashSet<int>();
        var operationIdentities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var decision in batch.Decisions
            .OrderBy(decision => decision.Operation.ExecutionOrder)
            .ThenBy(decision => decision.OperationIdentity, StringComparer.Ordinal))
        {
            if (decision.Status != OperationAuthorizationStatus.Authorized)
            {
                rejected.Add(decision.Operation);
                blockers.Add(decision.Reason);
                continue;
            }

            if (!decision.FutureAutomaticExecutionAllowed || decision.Operation.Action == DryRunAction.ManualReview || decision.Operation.SafetyState is DryRunSafetyState.ManualReview or DryRunSafetyState.Unsupported or DryRunSafetyState.Unverifiable)
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Operation {decision.OperationIdentity} cannot execute automatically.");
                continue;
            }

            if (decision.MatchedEvidence is null)
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Operation {decision.OperationIdentity} is missing ownership evidence.");
                continue;
            }

            if (decision.MatchedEvidence.SessionId != activeSessionId)
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Operation {decision.OperationIdentity} session does not match the active transaction session.");
                continue;
            }

            if (!executionOrders.Add(decision.Operation.ExecutionOrder))
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Duplicate execution sequence detected for operation {decision.OperationIdentity}.");
                continue;
            }

            if (!operationIdentities.Add(decision.OperationIdentity))
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Duplicate operation identity detected for {decision.OperationIdentity}.");
                continue;
            }

            if (!AuthorizedRestorationRequest.TryCreate(
                    decision.Operation,
                    decision,
                    activeTransactionId,
                    activeSessionId,
                    out var request,
                    out var validationFailure))
            {
                rejected.Add(decision.Operation);
                blockers.Add($"Operation {decision.OperationIdentity} rejected before execution: {validationFailure}");
                continue;
            }

            requested.Add(request!);
        }

        var blockingReasons = blockers
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reason => reason, StringComparer.Ordinal)
            .ToArray();

        var totalDecisionCount = batch.Decisions.Count;
        var canExecuteAutomatically = totalDecisionCount == 0 || (
            totalDecisionCount == requested.Count &&
            batch.Decisions.All(decision => decision.Status == OperationAuthorizationStatus.Authorized && decision.FutureAutomaticExecutionAllowed) &&
            blockingReasons.Length == 0);

        return new RestorationExecutionPreparation(requested, rejected, blockingReasons, canExecuteAutomatically);
    }
}
