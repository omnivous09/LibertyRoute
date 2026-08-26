using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

/// <summary>
/// One-shot atomic composition handoff from an exact Phase 4G activation grant to the
/// existing Phase 3E4 provider gate. Grant consumption, cancellation, capability
/// lifetime, and provider construction are contained in this synchronous call. The
/// capability never leaves the stack-local gate invocation.
/// </summary>
internal sealed class ControlledRestorationActivationHandoff
    : IControlledRestorationProviderGate
{
    private readonly ControlledRestorationActivationGrant _grant;
    private readonly IRestorationMutationProviderFactory _providerFactory;
    private readonly Action? _onGrantConsumed;

    internal ControlledRestorationActivationHandoff(
        ControlledRestorationActivationGrant grant)
        : this(grant, new RouteMutationProviderFactory())
    {
    }

    internal ControlledRestorationActivationHandoff(
        ControlledRestorationActivationGrant grant,
        IRestorationMutationProviderFactory providerFactory,
        Action? onGrantConsumed = null)
    {
        _grant = grant ?? throw new ArgumentNullException(nameof(grant));
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _onGrantConsumed = onGrantConsumed;
    }

    public RestorationExecutionPreflight Create(
        RestorationOrchestrationPreparation preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        ControlledRestorationGrantConsumption consumption;
        try
        {
            // Consume performs its atomic terminal transition before observing
            // cancellation or validating the preparation. Every winning path burns the
            // grant and releases its exact outstanding reservation.
            consumption = _grant.Consume(preparation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (!consumption.IsConsumed)
        {
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.InvalidCapability,
                $"The activation grant could not be consumed: {consumption.Reason}",
                null);
        }

        // Deterministic test hook for cancellation in the otherwise synchronous gap
        // after authority destruction and before capability creation.
        _onGrantConsumed?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        // This is the capability's entire lifetime. It is not placed in a field,
        // returned, cached, persisted, registered, or exposed through another result.
        var capability = RestorationExecutionCapability.CreateForActivationHandoff();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return _providerFactory.Create(
                preparation.ExecutionPreparation,
                preparation.ActiveSessionId,
                capability);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.ProviderConstructionFailed,
                $"Provider construction failed after activation authority was consumed ({exception.GetType().Name}: {exception.Message}).",
                null);
        }
    }
}
