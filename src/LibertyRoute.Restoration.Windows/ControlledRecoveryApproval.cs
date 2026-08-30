using System.Collections.Concurrent;
using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

internal enum ControlledRecoveryCandidateStatus
{
    Available,
    NoUnfinishedRecovery,
    RecoveryNotRequired,
    InvalidJournal
}

internal sealed record ControlledRecoveryCandidate(
    string CandidateId,
    Guid SessionId,
    ConnectionState RecoveryState,
    DateTimeOffset StartedAtUtc,
    string Reason);

internal sealed record ControlledRecoveryCandidateResult(
    ControlledRecoveryCandidateStatus Status,
    ControlledRecoveryCandidate? Candidate,
    string Reason);

internal sealed record ControlledRecoveryApprovalRequest(string CandidateId, Guid SessionId);

internal enum ControlledRecoveryApprovalStatus
{
    Approved,
    InvalidRequest,
    CandidateUnavailable,
    CandidateAlreadyTerminal,
    JournalChanged,
    RecoveryNotRequired
}

internal sealed record ControlledRecoveryApprovalDecision(
    ControlledRecoveryApprovalStatus Status,
    ControlledRecoveryApprovalTicket? Ticket,
    string Reason);

internal sealed class ControlledRecoveryCandidateReservation
{
    internal ControlledRecoveryCandidateReservation(
        string candidateId,
        Guid sessionId,
        ConnectionState state,
        DateTimeOffset startedAtUtc,
        string journalFingerprint)
    {
        CandidateId = candidateId;
        SessionId = sessionId;
        State = state;
        StartedAtUtc = startedAtUtc;
        JournalFingerprint = journalFingerprint;
    }

    internal string CandidateId { get; }
    internal Guid SessionId { get; }
    internal ConnectionState State { get; }
    internal DateTimeOffset StartedAtUtc { get; }
    internal string JournalFingerprint { get; }
    internal int Terminal;
}

internal sealed class ControlledRecoveryApprovalTicket : IDisposable
{
    private int _terminal;

    internal ControlledRecoveryApprovalTicket(
        object owner,
        Guid sessionId,
        string journalFingerprint)
    {
        Owner = owner;
        SessionId = sessionId;
        JournalFingerprint = journalFingerprint;
    }

    internal object Owner { get; }
    internal Guid SessionId { get; }
    internal string JournalFingerprint { get; }
    internal bool IsTerminal => Volatile.Read(ref _terminal) != 0;
    internal bool TryConsume() => Interlocked.CompareExchange(ref _terminal, 1, 0) == 0;
    public void Dispose() => Interlocked.CompareExchange(ref _terminal, 1, 0);
}

internal sealed record ControlledRecoveryApprovalProof(
    Guid SessionId,
    string JournalFingerprint)
{
    private int _terminal;
    private string? _preparationFingerprint;

    internal void BindPreparation(RestorationOrchestrationPreparation preparation)
    {
        if (Volatile.Read(ref _terminal) != 0)
            throw new InvalidOperationException("The recovery approval proof is already terminal.");
        if (!ControlledRestorationPreparationFingerprint.TryCreate(
                preparation,
                out var fingerprint,
                out var failureReason))
            throw new InvalidOperationException($"The approved recovery preparation cannot be bound: {failureReason}");
        var existing = Interlocked.CompareExchange(ref _preparationFingerprint, fingerprint, null);
        if (existing is not null && !StringComparer.Ordinal.Equals(existing, fingerprint))
            throw new InvalidOperationException("The recovery approval proof is already bound to a different preparation.");
    }

    internal bool TryConsume(out string? preparationFingerprint)
    {
        preparationFingerprint = null;
        if (Interlocked.CompareExchange(ref _terminal, 1, 0) != 0)
            return false;
        preparationFingerprint = Volatile.Read(ref _preparationFingerprint);
        return true;
    }
}

internal sealed class ControlledRecoveryApprovalAuthority
{
    private readonly record struct CandidateKey(Guid SessionId, string JournalFingerprint);

    private readonly object _owner = new();
    private readonly ITransactionJournal _journal;
    private readonly ConcurrentDictionary<CandidateKey, ControlledRecoveryCandidateReservation> _byKey = new();
    private readonly ConcurrentDictionary<string, ControlledRecoveryCandidateReservation> _byId = new(StringComparer.Ordinal);

    internal ControlledRecoveryApprovalAuthority(ITransactionJournal journal)
        => _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    internal async Task<ControlledRecoveryCandidateResult> QueryCandidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NetworkTransaction? journal;
        try
        {
            journal = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            return new(ControlledRecoveryCandidateStatus.InvalidJournal, null, exception.Message);
        }

        if (journal is null)
            return new(ControlledRecoveryCandidateStatus.NoUnfinishedRecovery, null, "No active recovery journal exists.");
        if (!RecoveryManager.NeedsRecovery(journal))
            return new(ControlledRecoveryCandidateStatus.RecoveryNotRequired, null, "The active journal no longer requires recovery.");

        var fingerprint = ControlledRecoveryJournalMarker.Create(journal).Sha256;
        var key = new CandidateKey(journal.SessionId, fingerprint);
        var created = new ControlledRecoveryCandidateReservation(
            Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            journal.SessionId,
            journal.State,
            journal.StartedAtUtc,
            fingerprint);
        var reservation = _byKey.GetOrAdd(key, created);
        var ownsReservation = ReferenceEquals(created, reservation);
        if (ownsReservation)
            _byId.TryAdd(reservation.CandidateId, reservation);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            if (ownsReservation && Interlocked.CompareExchange(ref reservation.Terminal, 1, 0) == 0)
                RemoveReservation(reservation);
            throw;
        }
        return new(
            ControlledRecoveryCandidateStatus.Available,
            new ControlledRecoveryCandidate(
                reservation.CandidateId,
                reservation.SessionId,
                reservation.State,
                reservation.StartedAtUtc,
                "An exact unfinished LibertyRoute recovery session is available for explicit approval."),
            "Recovery candidate available.");
    }

    internal async Task<ControlledRecoveryApprovalDecision> ApproveAsync(
        ControlledRecoveryApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(request.CandidateId))
            return new(ControlledRecoveryApprovalStatus.InvalidRequest, null, "Candidate id and session id are required.");
        if (!_byId.TryGetValue(request.CandidateId, out var reservation))
            return new(ControlledRecoveryApprovalStatus.CandidateUnavailable, null, "The exact recovery candidate is unavailable.");
        if (Interlocked.CompareExchange(ref reservation.Terminal, 1, 0) != 0)
            return new(ControlledRecoveryApprovalStatus.CandidateAlreadyTerminal, null, "The recovery candidate was already approved or revoked.");

        RemoveReservation(reservation);
        cancellationToken.ThrowIfCancellationRequested();
        if (reservation.SessionId != request.SessionId)
            return new(ControlledRecoveryApprovalStatus.CandidateUnavailable, null, "The recovery candidate session does not match the approval request.");
        var journal = await _journal.ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (journal is null || journal.SessionId != reservation.SessionId)
            return new(ControlledRecoveryApprovalStatus.JournalChanged, null, "The active recovery journal no longer matches the candidate.");
        if (!RecoveryManager.NeedsRecovery(journal))
            return new(ControlledRecoveryApprovalStatus.RecoveryNotRequired, null, "The active journal no longer requires recovery.");
        if (!StringComparer.Ordinal.Equals(
                ControlledRecoveryJournalMarker.Create(journal).Sha256,
                reservation.JournalFingerprint))
            return new(ControlledRecoveryApprovalStatus.JournalChanged, null, "The active recovery journal payload changed after presentation.");

        return new(
            ControlledRecoveryApprovalStatus.Approved,
            new ControlledRecoveryApprovalTicket(_owner, reservation.SessionId, reservation.JournalFingerprint),
            "One exact recovery attempt was approved.");
    }

    internal ControlledRecoveryApprovalProof Consume(
        ControlledRecoveryApprovalTicket ticket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!ticket.TryConsume())
            throw new InvalidOperationException("The recovery approval ticket is already consumed or revoked.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(ticket.Owner, _owner))
            throw new InvalidOperationException("The recovery approval belongs to another service authority instance.");
        return new ControlledRecoveryApprovalProof(ticket.SessionId, ticket.JournalFingerprint);
    }

    private void RemoveReservation(ControlledRecoveryCandidateReservation reservation)
    {
        ((ICollection<KeyValuePair<string, ControlledRecoveryCandidateReservation>>)_byId)
            .Remove(new(reservation.CandidateId, reservation));
        ((ICollection<KeyValuePair<CandidateKey, ControlledRecoveryCandidateReservation>>)_byKey)
            .Remove(new(new CandidateKey(reservation.SessionId, reservation.JournalFingerprint), reservation));
    }
}

internal sealed class ApprovedRecoveryActivationTrigger : IControlledRestorationActivationTrigger
{
    private readonly ControlledRecoveryApprovalProof _proof;

    internal ApprovedRecoveryActivationTrigger(ControlledRecoveryApprovalProof proof)
        => _proof = proof ?? throw new ArgumentNullException(nameof(proof));

    public ControlledRestorationTriggerDecision Evaluate(RestorationOrchestrationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!_proof.TryConsume(out var expectedFingerprint))
            return new(ControlledRestorationTriggerStatus.Denied, "The recovery approval proof is already terminal.");
        if (expectedFingerprint is null)
            return new(ControlledRestorationTriggerStatus.Denied, "The recovery approval proof was not bound to a prepared recovery batch.");
        if (preparation.ActiveSessionId != _proof.SessionId)
            return new(ControlledRestorationTriggerStatus.Denied, "The prepared recovery session does not match the approval.");
        if (!ControlledRestorationPreparationFingerprint.TryCreate(
                preparation,
                out var actualFingerprint,
                out var failureReason))
            return new(ControlledRestorationTriggerStatus.Denied, $"The prepared recovery batch is invalid: {failureReason}");
        if (!StringComparer.Ordinal.Equals(expectedFingerprint, actualFingerprint))
            return new(ControlledRestorationTriggerStatus.Denied, "The prepared recovery batch does not exactly match the approved preparation.");
        return new(ControlledRestorationTriggerStatus.Authorized, "The exact one-shot recovery approval authorized this preparation.");
    }
}

internal sealed class ControlledApprovedRecoveryExecution
{
    private readonly ControlledRecoveryApprovalAuthority _approvalAuthority;
    private readonly IRecoveryTransactionJournal _journal;
    private readonly INetworkStateManager _network;
    private readonly IRestorationExecutionOrchestrator _orchestrator;
    private readonly IRecordedMutationExecutorFactory _executorFactory;
    private readonly IRestorationMutationProviderFactory _providerFactory;
    private readonly IConditionalOwnershipLedger _conditionalLedger;

    internal ControlledApprovedRecoveryExecution(
        ControlledRecoveryApprovalAuthority approvalAuthority,
        IRecoveryTransactionJournal journal,
        IConditionalOwnershipLedger ledger,
        INetworkStateManager network,
        IRestorationExecutionOrchestrator orchestrator,
        IRecordedMutationExecutorFactory executorFactory,
        IRestorationMutationProviderFactory providerFactory)
    {
        _approvalAuthority = approvalAuthority ?? throw new ArgumentNullException(nameof(approvalAuthority));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _conditionalLedger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    internal Task<RecoveryRestorationWorkflowResult> ExecuteAsync(
        ControlledRecoveryApprovalTicket ticket,
        CancellationToken cancellationToken)
    {
        var proof = _approvalAuthority.Consume(ticket, cancellationToken);
        var authority = new ControlledRestorationActivationAuthority(new ApprovedRecoveryActivationTrigger(proof));
        var workflow = new ControlledRecoveryRestorationWorkflow(
            _journal, _conditionalLedger, _network, _orchestrator, _executorFactory,
            authority, _providerFactory, proof.BindPreparation);
        return workflow.ExecuteAsync(
            new RecoveryRestorationRequest(proof.SessionId, proof.JournalFingerprint),
            cancellationToken);
    }
}
