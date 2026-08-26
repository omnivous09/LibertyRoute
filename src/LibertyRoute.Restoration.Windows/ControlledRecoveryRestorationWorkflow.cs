using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

internal sealed record RecoveryRestorationRequest(
    Guid SessionId,
    string? ExpectedJournalFingerprint = null);

internal readonly record struct ControlledRecoveryJournalMarker(string Sha256)
{
    internal static ControlledRecoveryJournalMarker Create(NetworkTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var json = JsonSerializer.Serialize(transaction);
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))));
    }
}

internal enum RecoveryRestorationWorkflowStatus
{
    NoUnfinishedRecovery,
    InvalidRequest,
    JournalSessionMismatch,
    RecoveryNotRequired,
    JournalChanged,
    PreparationBlocked,
    ActivationDenied,
    ExecutionReturned
}

internal sealed record RecoveryRestorationWorkflowResult(
    RecoveryRestorationWorkflowStatus Status,
    Guid? SessionId,
    ConnectionState? RecoveryState,
    int PlannedOperationCount,
    IReadOnlyList<string> BlockingReasons,
    ControlledRestorationActivationStatus? ActivationStatus,
    ControlledRestorationExecutionResult? Execution,
    string Reason);

/// <summary>
/// Internal, unregistered recovery integration workflow. It reconstructs a dry-run
/// plan from the journal baseline and a fresh read-only snapshot, then delegates all
/// ownership authorization to Phase 4E. Its production authority remains deny-only.
/// </summary>
internal sealed class ControlledRecoveryRestorationWorkflow
{
    private sealed class SessionLease
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);
        internal int Users { get; set; }
        internal bool Removed { get; set; }
    }

    private static readonly ConcurrentDictionary<Guid, SessionLease> SessionLeases = new();
    private readonly ITransactionJournal _journal;
    private readonly INetworkStateManager _network;
    private readonly IRestorationExecutionOrchestrator _orchestrator;
    private readonly IRecordedMutationExecutorFactory _executorFactory;
    private readonly IControlledRestorationActivationAuthority _authority;
    private readonly IRestorationMutationProviderFactory _providerFactory;
    private readonly Action<RestorationOrchestrationPreparation>? _bindApprovedPreparation;

    internal ControlledRecoveryRestorationWorkflow(
        ITransactionJournal journal,
        INetworkStateManager network,
        IRestorationExecutionOrchestrator orchestrator,
        IRecordedMutationExecutorFactory executorFactory)
        : this(
            journal,
            network,
            orchestrator,
            executorFactory,
            new ControlledRestorationActivationAuthority(),
            new RouteMutationProviderFactory())
    {
    }

    internal ControlledRecoveryRestorationWorkflow(
        ITransactionJournal journal,
        INetworkStateManager network,
        IRestorationExecutionOrchestrator orchestrator,
        IRecordedMutationExecutorFactory executorFactory,
        IControlledRestorationActivationAuthority authority,
        IRestorationMutationProviderFactory providerFactory,
        Action<RestorationOrchestrationPreparation>? bindApprovedPreparation = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _bindApprovedPreparation = bindApprovedPreparation;
    }

    internal async Task<RecoveryRestorationWorkflowResult> ExecuteAsync(
        RecoveryRestorationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty)
            return Result(RecoveryRestorationWorkflowStatus.InvalidRequest, null, null, "A recovery session id is required.");

        cancellationToken.ThrowIfCancellationRequested();
        var sessionLease = await AcquireSessionLeaseAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        try
        {
            var journal = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            var initialValidation = ValidateJournal(request, journal);
            if (initialValidation is not null)
                return initialValidation;

            var marker = ControlledRecoveryJournalMarker.Create(journal!);
            if (request.ExpectedJournalFingerprint is not null &&
                !StringComparer.Ordinal.Equals(request.ExpectedJournalFingerprint, marker.Sha256))
            {
                return Result(RecoveryRestorationWorkflowStatus.JournalChanged, request.SessionId, journal!.State, "The active recovery journal no longer matches the explicitly approved candidate.");
            }
            var current = await _network.CaptureStateAsync(cancellationToken).ConfigureAwait(false);

            journal = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            var postCaptureValidation = ValidateJournal(request, journal);
            if (postCaptureValidation is not null)
                return postCaptureValidation;
            if (!marker.Equals(ControlledRecoveryJournalMarker.Create(journal!)))
                return Result(RecoveryRestorationWorkflowStatus.JournalChanged, request.SessionId, journal?.State, "The active recovery journal changed while current state was captured.");

            var dryRun = DryRunRestorationExecutor.CreateDryRun(
                RestorationPlanner.CreatePlan(journal!.Snapshot, current));
            var preparation = await _orchestrator.PrepareAsync(
                dryRun,
                Guid.NewGuid(),
                journal.SessionId,
                cancellationToken).ConfigureAwait(false);

            var blockers = preparation.ExecutionPreparation.BlockingReasons;
            var operationCount = dryRun.Operations.Count;
            if (!preparation.ExecutionPreparation.CanExecuteAutomatically ||
                preparation.ExecutionPreparation.AuthorizedRequests.Count == 0 ||
                preparation.ExecutionPreparation.RejectedOperations.Count != 0 ||
                blockers.Count != 0)
            {
                return new RecoveryRestorationWorkflowResult(
                    RecoveryRestorationWorkflowStatus.PreparationBlocked,
                    journal.SessionId,
                    journal.State,
                    operationCount,
                    blockers,
                    null,
                    null,
                    "The reconstructed recovery preparation is not wholly eligible for automatic execution.");
            }

            var currentJournal = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
            var preAuthorityValidation = ValidateJournal(request, currentJournal);
            if (preAuthorityValidation is not null)
                return preAuthorityValidation;
            if (!marker.Equals(ControlledRecoveryJournalMarker.Create(currentJournal!)))
                return Result(RecoveryRestorationWorkflowStatus.JournalChanged, request.SessionId, currentJournal?.State, "The active recovery journal changed before activation authorization.", operationCount);

            _bindApprovedPreparation?.Invoke(preparation);
            cancellationToken.ThrowIfCancellationRequested();
            var activation = await _authority.AuthorizeAsync(preparation, cancellationToken).ConfigureAwait(false);
            if (activation.Status != ControlledRestorationActivationStatus.Authorized || activation.Grant is null)
            {
                activation.Grant?.Dispose();
                return new RecoveryRestorationWorkflowResult(
                    RecoveryRestorationWorkflowStatus.ActivationDenied,
                    journal.SessionId,
                    journal.State,
                    operationCount,
                    blockers,
                    activation.Status,
                    null,
                    activation.Reason);
            }

            using var grant = activation.Grant;
            cancellationToken.ThrowIfCancellationRequested();
            var boundary = new ControlledWindowsRestorationExecutionBoundary(
                _orchestrator,
                _executorFactory,
                new ControlledRestorationActivationHandoff(grant, _providerFactory));
            var execution = await boundary.ExecuteAsync(preparation, cancellationToken).ConfigureAwait(false);
            return new RecoveryRestorationWorkflowResult(
                RecoveryRestorationWorkflowStatus.ExecutionReturned,
                journal.SessionId,
                journal.State,
                operationCount,
                blockers,
                activation.Status,
                execution,
                execution.Reason);
        }
        finally
        {
            ReleaseSessionLease(request.SessionId, sessionLease);
        }
    }

    private static async Task<SessionLease> AcquireSessionLeaseAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var lease = SessionLeases.GetOrAdd(sessionId, static _ => new SessionLease());
            lock (lease)
            {
                if (lease.Removed)
                    continue;
                lease.Users++;
            }

            try
            {
                await lease.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return lease;
            }
            catch
            {
                ReleaseSessionLeaseReference(sessionId, lease);
                throw;
            }
        }
    }

    private static void ReleaseSessionLease(Guid sessionId, SessionLease lease)
    {
        lease.Semaphore.Release();
        ReleaseSessionLeaseReference(sessionId, lease);
    }

    private static void ReleaseSessionLeaseReference(Guid sessionId, SessionLease lease)
    {
        lock (lease)
        {
            lease.Users--;
            if (lease.Users != 0)
                return;

            lease.Removed = true;
            var pair = new KeyValuePair<Guid, SessionLease>(sessionId, lease);
            ((ICollection<KeyValuePair<Guid, SessionLease>>)SessionLeases).Remove(pair);
        }
    }

    private static RecoveryRestorationWorkflowResult? ValidateJournal(
        RecoveryRestorationRequest request,
        NetworkTransaction? journal)
    {
        if (journal is null)
            return Result(RecoveryRestorationWorkflowStatus.NoUnfinishedRecovery, request.SessionId, null, "No active recovery journal exists.");
        if (journal.SessionId != request.SessionId)
            return Result(RecoveryRestorationWorkflowStatus.JournalSessionMismatch, request.SessionId, journal.State, "The selected recovery session does not match the active journal.");
        if (!RecoveryManager.NeedsRecovery(journal))
            return Result(RecoveryRestorationWorkflowStatus.RecoveryNotRequired, journal.SessionId, journal.State, "The active journal does not require recovery.");
        return null;
    }

    private static RecoveryRestorationWorkflowResult Result(
        RecoveryRestorationWorkflowStatus status,
        Guid? sessionId,
        ConnectionState? state,
        string reason,
        int operationCount = 0)
        => new(status, sessionId, state, operationCount, Array.Empty<string>(), null, null, reason);

}
