namespace LibertyRoute.Restoration;

/// <summary>
/// Session-bound execution surface for authorized mutation requests. This is the
/// production-capable Phase 4D orchestration boundary: callers supply an already
/// authorized request and receive the full <see cref="RecordedMutationExecution"/>
/// result without flattening ownership-recording failures into provider-only states.
///
/// <see cref="RecordRevertedAsync"/> is METADATA LIFECYCLE RECORDING ONLY. It does not
/// perform a network revert, does not verify that a network revert occurred, and only
/// advances an existing Applied ownership record to Reverted. A future controlled path
/// must call it only after independently established successful rollback/revert.
/// </summary>
public interface IRecordedMutationExecutor
{
    Guid ActiveSessionId { get; }

    Task<RecordedMutationExecution> ExecuteAsync(
        AuthorizedRestorationRequest request,
        CancellationToken cancellationToken);

    Task<RecordedMutationExecution> RecordRevertedAsync(
        Guid changeId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Factory for creating session-bound recorded mutation executors. The factory depends
/// only on the DI-owned ownership ledger and cannot resolve, discover, or construct a
/// provider itself. The caller must explicitly supply the provider at the controlled
/// call site, keeping normal Service startup unable to create a live execution path.
/// </summary>
public interface IRecordedMutationExecutorFactory
{
    IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider);
}

internal sealed class RecordedMutationExecutor : IRecordedMutationExecutor
{
    private readonly MutationOwnershipCoordinator _coordinator;

    internal RecordedMutationExecutor(
        Guid activeSessionId,
        IOwnershipLedger ledger,
        IRestorationMutationProvider provider)
    {
        if (activeSessionId == Guid.Empty)
            throw new ArgumentException("Active session id is required.", nameof(activeSessionId));

        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(provider);

        ActiveSessionId = activeSessionId;
        _coordinator = new MutationOwnershipCoordinator(ledger, provider);
    }

    public Guid ActiveSessionId { get; }

    public Task<RecordedMutationExecution> ExecuteAsync(
        AuthorizedRestorationRequest request,
        CancellationToken cancellationToken)
        => _coordinator.ExecuteAuthorizedMutationAsync(request, ActiveSessionId, cancellationToken);

    /// <summary>
    /// Metadata lifecycle recording only. No network rollback/revert is performed or
    /// verified here.
    /// </summary>
    public Task<RecordedMutationExecution> RecordRevertedAsync(
        Guid changeId,
        CancellationToken cancellationToken)
        => _coordinator.RecordRevertedAsync(ActiveSessionId, changeId, cancellationToken);
}

public sealed class RecordedMutationExecutorFactory : IRecordedMutationExecutorFactory
{
    private readonly IOwnershipLedger _ledger;

    public RecordedMutationExecutorFactory(IOwnershipLedger ledger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
    }

    public IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider)
    {
        if (activeSessionId == Guid.Empty)
            throw new ArgumentException("Active session id is required.", nameof(activeSessionId));

        ArgumentNullException.ThrowIfNull(provider);
        return new RecordedMutationExecutor(activeSessionId, _ledger, provider);
    }
}