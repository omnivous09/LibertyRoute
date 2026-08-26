using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

/// <summary>
/// Result of attempting to cross the controlled Windows restoration composition
/// boundary. The Phase 4E batch result is retained without translation when execution
/// begins. No provider, capability, or native object is exposed.
/// </summary>
public enum ControlledRestorationExecutionStatus
{
    GateRejected,
    ExecutorCreationFailed,
    ExecutionReturned
}

public sealed record ControlledRestorationExecutionResult(
    ControlledRestorationExecutionStatus Status,
    RestorationExecutionGateStatus GateStatus,
    RestorationBatchExecution? BatchExecution,
    string Reason);

public interface IControlledWindowsRestorationExecutionBoundary
{
    Task<ControlledRestorationExecutionResult> ExecuteAsync(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Internal seam representing a provider that has already passed the Phase 3E4 live
/// gate. Tests inject a controlled fake implementation. The production implementation
/// deliberately supplies no capability and therefore cannot construct a provider.
/// </summary>
internal interface IControlledRestorationProviderGate
{
    RestorationExecutionPreflight Create(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken);
}

internal sealed class CapabilityUnavailableRestorationProviderGate
    : IControlledRestorationProviderGate
{
    private readonly IRestorationMutationProviderFactory _providerFactory;

    internal CapabilityUnavailableRestorationProviderGate()
        : this(new RouteMutationProviderFactory())
    {
    }

    internal CapabilityUnavailableRestorationProviderGate(
        IRestorationMutationProviderFactory providerFactory)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    public RestorationExecutionPreflight Create(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        return _providerFactory.Create(
            preparation.ExecutionPreparation,
            preparation.ActiveSessionId,
            capability: null);
    }
}

/// <summary>
/// Production composition structure for one short-lived, session-bound restoration
/// attempt. Phase 4F intentionally has no production capability issuer: the public
/// constructor always uses a gate that supplies no capability, so normal production
/// calls stop before provider construction. A controlled gate is injectable only
/// through the internal test seam.
/// </summary>
public sealed class ControlledWindowsRestorationExecutionBoundary
    : IControlledWindowsRestorationExecutionBoundary
{
    private readonly IRestorationExecutionOrchestrator _orchestrator;
    private readonly IRecordedMutationExecutorFactory _executorFactory;
    private readonly IControlledRestorationProviderGate _providerGate;

    public ControlledWindowsRestorationExecutionBoundary(
        IRestorationExecutionOrchestrator orchestrator,
        IRecordedMutationExecutorFactory executorFactory)
        : this(
            orchestrator,
            executorFactory,
            new CapabilityUnavailableRestorationProviderGate())
    {
    }

    internal ControlledWindowsRestorationExecutionBoundary(
        IRestorationExecutionOrchestrator orchestrator,
        IRecordedMutationExecutorFactory executorFactory,
        IControlledRestorationProviderGate providerGate)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _providerGate = providerGate ?? throw new ArgumentNullException(nameof(providerGate));
    }

    public async Task<ControlledRestorationExecutionResult> ExecuteAsync(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();

        var preflight = _providerGate.Create(preparation, cancellationToken);
        if (!preflight.IsEnabled || preflight.Provider is null)
        {
            return new ControlledRestorationExecutionResult(
                ControlledRestorationExecutionStatus.GateRejected,
                preflight.Status,
                BatchExecution: null,
                preflight.Reason);
        }

        // Provider construction has no mutation side effect. Cancellation remains safe
        // until the Phase 4E orchestrator begins the first executor call.
        cancellationToken.ThrowIfCancellationRequested();

        IRecordedMutationExecutor executor;
        try
        {
            executor = _executorFactory.Create(preparation.ActiveSessionId, preflight.Provider);
        }
        catch (Exception exception)
        {
            return new ControlledRestorationExecutionResult(
                ControlledRestorationExecutionStatus.ExecutorCreationFailed,
                preflight.Status,
                BatchExecution: null,
                $"Recorded executor creation failed ({exception.GetType().Name}: {exception.Message}); no execution began.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var batchExecution = await _orchestrator.ExecutePreparedAsync(
            preparation,
            executor,
            cancellationToken).ConfigureAwait(false);

        return new ControlledRestorationExecutionResult(
            ControlledRestorationExecutionStatus.ExecutionReturned,
            preflight.Status,
            batchExecution,
            "The controlled execution boundary returned the Phase 4E batch result.");
    }
}
