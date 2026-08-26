namespace LibertyRoute.Restoration;

/// <summary>
/// Loads ownership evidence and applies the existing authorization and execution-
/// preparation contracts without acquiring a mutation provider or executor.
/// </summary>
public interface IRestorationExecutionOrchestrator
{
    Task<RestorationOrchestrationPreparation> PrepareAsync(
        DryRunRestorationResult dryRunResult,
        Guid activeTransactionId,
        Guid activeSessionId,
        CancellationToken cancellationToken);

    Task<RestorationBatchExecution> ExecutePreparedAsync(
        RestorationOrchestrationPreparation preparation,
        IRecordedMutationExecutor executor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Session binding for an existing <see cref="RestorationExecutionPreparation"/>.
/// Transaction identity is deliberately not retained: SessionId is the canonical
/// ownership identity, while the transaction id is used only by the existing request-
/// preparation contract that requires it.
/// </summary>
public sealed class RestorationOrchestrationPreparation
{
    public Guid ActiveSessionId { get; }
    public RestorationExecutionPreparation ExecutionPreparation { get; }

    internal RestorationOrchestrationPreparation(
        Guid activeSessionId,
        RestorationExecutionPreparation executionPreparation)
    {
        ActiveSessionId = activeSessionId;
        ExecutionPreparation = executionPreparation;
    }
}

public enum RestorationBatchExecutionStatus
{
    Completed,
    BlockedBeforeExecution,
    StoppedAfterUnsafeOutcome,
    CancelledAfterExecutionAttempt,
    CancelledAfterPartialExecution,
    ExecutorThrewAfterAttempt
}

/// <summary>
/// Metadata-only result for sequential batch execution. AttemptedRequests includes a
/// request as soon as its executor call begins, even if that call never returns a
/// RecordedMutationExecution.
/// </summary>
public sealed record RestorationBatchExecution(
    RestorationBatchExecutionStatus Status,
    IReadOnlyList<AuthorizedRestorationRequest> AttemptedRequests,
    IReadOnlyList<RecordedMutationExecution> CompletedExecutions,
    AuthorizedRestorationRequest? StoppedAtRequest,
    IReadOnlyList<AuthorizedRestorationRequest> NotAttemptedRequests,
    bool RequiresManualRecovery,
    string Reason);

public sealed class RestorationExecutionOrchestrator : IRestorationExecutionOrchestrator
{
    private readonly IOwnershipLedger _ledger;

    public RestorationExecutionOrchestrator(IOwnershipLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public async Task<RestorationOrchestrationPreparation> PrepareAsync(
        DryRunRestorationResult dryRunResult,
        Guid activeTransactionId,
        Guid activeSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dryRunResult);
        if (activeTransactionId == Guid.Empty)
            throw new ArgumentException("Active transaction id is required by execution request preparation.", nameof(activeTransactionId));
        if (activeSessionId == Guid.Empty)
            throw new ArgumentException("Active session id is required.", nameof(activeSessionId));

        var records = await _ledger.ReadForSessionAsync(activeSessionId, cancellationToken).ConfigureAwait(false);
        var evidence = records.Select(record => record.ToOwnershipEvidence()).ToArray();
        var authorization = RestorationAuthorizationPolicy.AuthorizeBatch(dryRunResult, evidence, activeSessionId);
        var executionPreparation = RestorationExecutionPreparation.Prepare(
            authorization,
            activeTransactionId,
            activeSessionId);

        return new RestorationOrchestrationPreparation(activeSessionId, executionPreparation);
    }

    public async Task<RestorationBatchExecution> ExecutePreparedAsync(
        RestorationOrchestrationPreparation preparation,
        IRecordedMutationExecutor executor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(executor);

        var requests = preparation.ExecutionPreparation.AuthorizedRequests;
        var blockingReason = ValidateForExecution(preparation, executor);
        if (blockingReason is not null)
        {
            return Result(
                RestorationBatchExecutionStatus.BlockedBeforeExecution,
                Array.Empty<AuthorizedRestorationRequest>(),
                Array.Empty<RecordedMutationExecution>(),
                null,
                requests,
                requiresManualRecovery: false,
                blockingReason);
        }

        var attempted = new List<AuthorizedRestorationRequest>();
        var completed = new List<RecordedMutationExecution>();

        foreach (var request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (attempted.Count == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                return Result(
                    RestorationBatchExecutionStatus.CancelledAfterPartialExecution,
                    attempted,
                    completed,
                    null,
                    requests.Skip(attempted.Count).ToArray(),
                    completed.Any(execution => execution.RequiresManualRecovery),
                    "Cancellation was requested after execution had begun; remaining requests were not attempted.");
            }

            attempted.Add(request);

            RecordedMutationExecution execution;
            try
            {
                execution = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                return Result(
                    completed.Count == 0
                        ? RestorationBatchExecutionStatus.CancelledAfterExecutionAttempt
                        : RestorationBatchExecutionStatus.CancelledAfterPartialExecution,
                    attempted,
                    completed,
                    request,
                    requests.Skip(attempted.Count).ToArray(),
                    requiresManualRecovery: true,
                    $"The executor call was cancelled after it began ({exception.Message}); its mutation state is not represented by a RecordedMutationExecution.");
            }
            catch (Exception exception)
            {
                return Result(
                    RestorationBatchExecutionStatus.ExecutorThrewAfterAttempt,
                    attempted,
                    completed,
                    request,
                    requests.Skip(attempted.Count).ToArray(),
                    requiresManualRecovery: true,
                    $"The executor threw {exception.GetType().Name} after the call began: {exception.Message}");
            }

            completed.Add(execution);
            if (!PermitsContinuation(execution.Outcome))
            {
                return Result(
                    RestorationBatchExecutionStatus.StoppedAfterUnsafeOutcome,
                    attempted,
                    completed,
                    request,
                    requests.Skip(attempted.Count).ToArray(),
                    completed.Any(item => item.RequiresManualRecovery),
                    $"Execution stopped after outcome {execution.Outcome}: {execution.Reason}");
            }
        }

        return Result(
            RestorationBatchExecutionStatus.Completed,
            attempted,
            completed,
            null,
            Array.Empty<AuthorizedRestorationRequest>(),
            completed.Any(execution => execution.RequiresManualRecovery),
            "Every prepared request completed with ownership recorded as Applied.");
    }

    private static string? ValidateForExecution(
        RestorationOrchestrationPreparation preparation,
        IRecordedMutationExecutor executor)
    {
        var executionPreparation = preparation.ExecutionPreparation;
        if (preparation.ActiveSessionId == Guid.Empty)
            return "The preparation has no active session.";
        if (executor.ActiveSessionId != preparation.ActiveSessionId)
            return "The executor session does not match the prepared session.";
        if (!executionPreparation.CanExecuteAutomatically)
            return "The complete preparation is not eligible for automatic execution.";
        if (executionPreparation.RejectedOperations.Count != 0 || executionPreparation.BlockingReasons.Count != 0)
            return "The preparation contains rejected operations or blocking reasons.";
        if (executionPreparation.AuthorizedRequests.Count == 0)
            return "The preparation contains no authorized requests.";
        if (executionPreparation.AuthorizedRequests.Any(request => request.SessionId != preparation.ActiveSessionId))
            return "A prepared request does not match the active session.";
        if (executionPreparation.AuthorizedRequests.Select(request => request.ExecutionOrder).Distinct().Count() != executionPreparation.AuthorizedRequests.Count)
            return "The preparation contains duplicate execution order values.";
        if (executionPreparation.AuthorizedRequests.Select(request => request.OperationIdentity).Distinct(StringComparer.Ordinal).Count() != executionPreparation.AuthorizedRequests.Count)
            return "The preparation contains duplicate operation identities.";

        return null;
    }

    private static bool PermitsContinuation(RecordedMutationOutcome outcome)
        => outcome is RecordedMutationOutcome.ExecutedAndApplied
            or RecordedMutationOutcome.AlreadyAppliedNoProviderCall;

    private static RestorationBatchExecution Result(
        RestorationBatchExecutionStatus status,
        IEnumerable<AuthorizedRestorationRequest> attemptedRequests,
        IEnumerable<RecordedMutationExecution> completedExecutions,
        AuthorizedRestorationRequest? stoppedAtRequest,
        IEnumerable<AuthorizedRestorationRequest> notAttemptedRequests,
        bool requiresManualRecovery,
        string reason)
        => new(
            status,
            attemptedRequests.ToArray(),
            completedExecutions.ToArray(),
            stoppedAtRequest,
            notAttemptedRequests.ToArray(),
            requiresManualRecovery,
            reason);
}
