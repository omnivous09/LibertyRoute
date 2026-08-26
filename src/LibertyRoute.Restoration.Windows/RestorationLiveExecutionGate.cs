using LibertyRoute.Restoration;

namespace LibertyRoute.Restoration.Windows;

public enum RestorationExecutionGateStatus
{
    Enabled,
    Disabled,
    BlockedByAuthorization,
    BlockedByBatch,
    InvalidCapability,
    SessionMismatch,
    ProviderConstructionFailed
}

public sealed record RestorationExecutionPreflight(
    RestorationExecutionGateStatus Status,
    string Reason,
    IRestorationMutationProvider? Provider)
{
    public bool IsEnabled => Status == RestorationExecutionGateStatus.Enabled;
}

internal sealed class RestorationExecutionCapability
{
    private static readonly object ValidMarker = new();
    private readonly object _marker;

    private RestorationExecutionCapability(object marker)
    {
        _marker = marker;
    }

    internal bool IsValid => ReferenceEquals(_marker, ValidMarker);

    internal static RestorationExecutionCapability CreateForControlledTest()
        => new(ValidMarker);

    internal static RestorationExecutionCapability CreateInvalidForControlledTest()
        => new(new object());

    /// <summary>
    /// Narrow production seam used only by the preparation-bound, one-shot activation
    /// handoff. The returned token must remain local to that handoff's synchronous
    /// provider-gate call and must never be exposed, stored, cached, or registered.
    /// </summary>
    internal static RestorationExecutionCapability CreateForActivationHandoff()
        => new(ValidMarker);
}

internal interface IRestorationMutationProviderFactory
{
    RestorationExecutionPreflight Create(
        RestorationExecutionPreparation preparation,
        Guid activeSessionId,
        RestorationExecutionCapability? capability);
}

internal sealed class RouteMutationProviderFactory : IRestorationMutationProviderFactory
{
    private readonly Func<IRestorationMutationProvider> _providerFactory;

    internal RouteMutationProviderFactory()
        : this(static () => new RouteRestorationProvider(new WindowsRouteMutationNative()))
    {
    }

    internal RouteMutationProviderFactory(Func<IRestorationMutationProvider> providerFactory)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    public RestorationExecutionPreflight Create(
        RestorationExecutionPreparation preparation,
        Guid activeSessionId,
        RestorationExecutionCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        if (capability is null)
            return Disabled("Live route mutation capability was not supplied.");

        if (!capability.IsValid)
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.InvalidCapability,
                "The live route mutation capability is invalid.",
                null);

        if (!preparation.CanExecuteAutomatically)
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.BlockedByBatch,
                "The prepared restoration batch is not eligible for automatic execution.",
                null);

        if (preparation.AuthorizedRequests.Count == 0)
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.BlockedByAuthorization,
                "The prepared restoration batch contains no authorized requests.",
                null);

        if (preparation.AuthorizedRequests.Any(request => request.SessionId != activeSessionId))
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.SessionMismatch,
                "A prepared restoration request does not match the active session.",
                null);

        var duplicateIdentity = preparation.AuthorizedRequests
            .GroupBy(request => request.OperationIdentity, StringComparer.Ordinal)
            .Any(group => group.Count() > 1);
        var duplicateOrder = preparation.AuthorizedRequests
            .GroupBy(request => request.ExecutionOrder)
            .Any(group => group.Count() > 1);
        if (duplicateIdentity || duplicateOrder)
            return new RestorationExecutionPreflight(
                RestorationExecutionGateStatus.BlockedByAuthorization,
                "The prepared restoration batch contains duplicate operation identity or execution order values.",
                null);

        return new RestorationExecutionPreflight(
            RestorationExecutionGateStatus.Enabled,
            "The prepared restoration batch passed live-execution preflight.",
            _providerFactory());
    }

    private static RestorationExecutionPreflight Disabled(string reason)
        => new(RestorationExecutionGateStatus.Disabled, reason, null);
}
