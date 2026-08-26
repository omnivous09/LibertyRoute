using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;

namespace LibertyRoute.Service;

/// <summary>
/// Production composition for the LibertyRoute network service.
///
/// Phase 4C boundary: this registration intentionally contains NO mutation-related
/// services. No mutation provider, route restoration provider, native route adapter,
/// mutation-provider factory, live-execution capability, or ownership-recording
/// coordinator is registered here. Normal service startup therefore cannot construct
/// or invoke a live mutation provider; the internal Phase 3E4 capability/gate remains
/// the only path to one and is unreachable from production code.
///
/// The ownership ledger is registered as a singleton: the Service is intended to be the
/// sole future production writer of ownership evidence, preserving the Phase 4A
/// single-writer model. Resolving the ledger constructs FileOwnershipLedger, whose only
/// side effect is ensuring its storage directory exists; no ownership record is created,
/// cleared, or read during startup.
///
/// Both the public and the test-restricted entry points delegate to one shared
/// registration method, so the two compositions cannot drift apart.
/// </summary>
public static class LibertyRouteRegistration
{
    public static IServiceCollection AddLibertyRouteCoreServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddCoreRegistrations(services);
        services.AddSingleton<IOwnershipLedger, FileOwnershipLedger>();
        return services;
    }

    /// <summary>
    /// Test-restricted seam (visible only to LibertyRoute.Restoration.Tests through
    /// InternalsVisibleTo): identical composition, but the ownership ledger is rooted at
    /// the supplied temporary directory so composition tests never touch real
    /// ProgramData storage. Production callers cannot redirect ownership evidence
    /// storage; the public registration API is parameterless and canonical.
    /// </summary>
    internal static IServiceCollection AddLibertyRouteCoreServicesForTests(this IServiceCollection services, string ownershipLedgerRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownershipLedgerRoot);

        AddCoreRegistrations(services);
        services.AddSingleton<IOwnershipLedger>(_ => new FileOwnershipLedger(ownershipLedgerRoot));
        return services;
    }

    /// <summary>
    /// The single source of truth for every registration except the ownership ledger,
    /// whose storage root intentionally differs between the production and test entry
    /// points. Keeping all shared registrations here prevents composition drift.
    /// </summary>
    private static void AddCoreRegistrations(IServiceCollection services)
    {
        services.AddSingleton<INetworkStateManager, WindowsNetworkStateManager>();
        services.AddSingleton<ITransactionJournal, FileTransactionJournal>();
        services.AddSingleton<RecoveryManager>();
        services.AddSingleton<IConnectionEngine, WireGuardEngine>();
        services.AddSingleton<ConnectionController>();
        services.AddSingleton<IRecordedMutationExecutorFactory, RecordedMutationExecutorFactory>();
        services.AddHostedService<LibertyRouteWorker>();
    }
}
