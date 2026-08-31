using System.Reflection;
using System.Security.Principal;
using LibertyRoute.ControlProtocol;
using LibertyRoute.Core;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

/// <summary>
/// Phase 4C production-composition guards: the Service owns the ownership-ledger
/// infrastructure while remaining structurally unable to construct or invoke any live
/// mutation provider, native adapter, or execution capability.
/// </summary>
public sealed class ServiceCompositionTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;

        return directory is null
            ? throw new InvalidOperationException("Repository root was not found.")
            : directory.FullName;
    }

    private static string ReadSource(params string[] relativePath)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relativePath).ToArray()));

    private static string[] ProductionSourcesExcludingRestoration()
        => new[] { "LibertyRoute.Service", "LibertyRoute.Desktop", "LibertyRoute.Recovery", "LibertyRoute.Networking", "LibertyRoute.Engine" }
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "src", project),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

    // --- Project-reference graph ---

    [Fact]
    public void ServiceProjectReferencesRestorationWindowsOnlyForStartupReconciliation()
    {
        var csproj = ReadSource("src", "LibertyRoute.Service", "LibertyRoute.Service.csproj");
        Assert.Contains("LibertyRoute.Restoration.csproj", csproj, StringComparison.Ordinal);
        Assert.Contains("LibertyRoute.Restoration.Windows", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryProjectDoesNotReferenceRestorationProjects()
    {
        var csproj = ReadSource("src", "LibertyRoute.Recovery", "LibertyRoute.Recovery.csproj");
        Assert.DoesNotContain("LibertyRoute.Restoration", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopProjectDoesNotReferenceRestorationProjects()
    {
        var csproj = ReadSource("src", "LibertyRoute.Desktop", "LibertyRoute.Desktop.csproj");
        Assert.DoesNotContain("LibertyRoute.Restoration", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceAssemblyReferencesRestorationAndStartupReconciliationAssembly()
    {
        var referenced = typeof(LibertyRouteRegistration).Assembly
            .GetReferencedAssemblies()
            .Select(assemblyName => assemblyName.Name)
            .ToArray();

        Assert.Contains("LibertyRoute.Restoration", referenced);
        Assert.Contains("LibertyRoute.Restoration.Windows", referenced);
    }

    // --- DI composition ---

    [Fact]
    public void OwnershipLedgerIsRegisteredExactlyOnceAsSingletonFileImplementation()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(IOwnershipLedger)).ToArray();

        Assert.Single(descriptors);
        Assert.Equal(ServiceLifetime.Singleton, descriptors[0].Lifetime);
        Assert.NotNull(descriptors[0].ImplementationFactory);
    }

    [Fact]
    public async Task DiContainerResolvesLedgerWithoutCreatingRecordsOrMutationServices()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Composition", Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLibertyRouteCoreServicesForTests(tempRoot);

            await using var provider = services.BuildServiceProvider();

            var ledger = provider.GetRequiredService<IOwnershipLedger>();
            Assert.IsType<FileOwnershipLedger>(ledger);
            Assert.Same(ledger, provider.GetRequiredService<IOwnershipLedger>());

            // Startup side effects: directory creation only; no ownership record exists.
            Assert.Empty(Directory.GetFiles(tempRoot, "*.lrw", SearchOption.AllDirectories));
            Assert.Empty(await ledger.ReadForSessionAsync(Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Temp cleanup must never fail a test run.
            }
        }
    }

    [Fact]
    public void DiContainerContainsNoMutationRegistrations()
    {
        var forbiddenServiceTypes = new[]
        {
            typeof(IRestorationMutationProvider),
            typeof(RouteRestorationProvider),
            typeof(IRouteMutationNative),
            typeof(WindowsRouteMutationNative),
            typeof(WindowsRouteNativeApi),
            typeof(RouteMutationProviderFactory),
            typeof(RestorationExecutionCapability),
            typeof(MutationOwnershipCoordinator)
        };

        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        foreach (var forbiddenType in forbiddenServiceTypes)
        {
            Assert.DoesNotContain(services, descriptor =>
                descriptor.ServiceType == forbiddenType || descriptor.ImplementationType == forbiddenType);
        }
    }

    [Fact]
    public async Task ResolvingMutationProviderFromContainerFails()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServicesForTests(Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Composition", Guid.NewGuid().ToString("N")));

        await using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IRestorationMutationProvider>());
    }

    [Fact]
    public async Task RecordedMutationExecutorFactoryIsRegisteredExactlyOnceInProductionAndTestComposition()
    {
        var productionRoot = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Composition", Guid.NewGuid().ToString("N"));
        var testRoot = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Composition", Guid.NewGuid().ToString("N"));
        try
        {
            var productionServices = new ServiceCollection();
            productionServices.AddLibertyRouteCoreServices();
            var productionDescriptor = Assert.Single(
                productionServices,
                descriptor => descriptor.ServiceType == typeof(IRecordedMutationExecutorFactory));
            Assert.Equal(typeof(RecordedMutationExecutorFactory), productionDescriptor.ImplementationType);

            // Redirect only the ledger storage so resolving the production composition
            // cannot touch the real ProgramData location during this test.
            productionServices.Remove(Assert.Single(
                productionServices,
                descriptor => descriptor.ServiceType == typeof(IOwnershipLedger)));
            productionServices.AddSingleton<IOwnershipLedger>(_ => new FileOwnershipLedger(productionRoot));

            var testServices = new ServiceCollection();
            testServices.AddLibertyRouteCoreServicesForTests(testRoot);
            var testDescriptor = Assert.Single(
                testServices,
                descriptor => descriptor.ServiceType == typeof(IRecordedMutationExecutorFactory));
            Assert.Equal(typeof(RecordedMutationExecutorFactory), testDescriptor.ImplementationType);

            await using var productionProvider = productionServices.BuildServiceProvider();
            await using var testProvider = testServices.BuildServiceProvider();

            Assert.IsType<RecordedMutationExecutorFactory>(
                productionProvider.GetRequiredService<IRecordedMutationExecutorFactory>());
            Assert.IsType<RecordedMutationExecutorFactory>(
                testProvider.GetRequiredService<IRecordedMutationExecutorFactory>());

            Assert.Empty(Directory.GetFiles(productionRoot, "*.lrw", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(testRoot, "*.lrw", SearchOption.AllDirectories));
            Assert.Throws<InvalidOperationException>(
                () => productionProvider.GetRequiredService<IRestorationMutationProvider>());
            Assert.Throws<InvalidOperationException>(
                () => testProvider.GetRequiredService<IRestorationMutationProvider>());
        }
        finally
        {
            foreach (var root in new[] { productionRoot, testRoot })
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    // --- Constructor dependency guards ---

    [Fact]
    public void ConnectionControllerHasNoCoordinatorLedgerOrProviderDependency()
    {
        var constructor = typeof(ConnectionController).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal(3, parameterTypes.Length);
        Assert.Contains(typeof(INetworkStateManager), parameterTypes);
        Assert.Contains(typeof(ITransactionJournal), parameterTypes);
        Assert.Contains(typeof(IConnectionEngine), parameterTypes);
        Assert.DoesNotContain(typeof(MutationOwnershipCoordinator), parameterTypes);
        Assert.DoesNotContain(typeof(IOwnershipLedger), parameterTypes);
        Assert.DoesNotContain(typeof(IRestorationMutationProvider), parameterTypes);
    }

    [Fact]
    public void RecoveryManagerHasNoMutationDependencies()
    {
        var constructor = typeof(RecoveryManager).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Single(parameterTypes);
        Assert.Contains(typeof(ITransactionJournal), parameterTypes);
        Assert.DoesNotContain(typeof(IOwnershipLedger), parameterTypes);
        Assert.DoesNotContain(typeof(IRestorationMutationProvider), parameterTypes);
        Assert.DoesNotContain(typeof(MutationOwnershipCoordinator), parameterTypes);
    }

    // --- Source-level production safety ---

    [Fact]
    public void ProductionSourcesContainNoMutationTypeOrNativeCallReferences()
    {
        var forbiddenIdentifiers = new[]
        {
            "WindowsRouteMutationNative",
            "WindowsRouteNativeApi",
            "RouteRestorationProvider",
            "IRouteMutationNative",
            "RouteMutationProviderFactory",
            "RestorationExecutionCapability",
            "CreateIpForwardEntry2",
            "DeleteIpForwardEntry2"
        };

        var sources = ProductionSourcesExcludingRestoration();
        Assert.NotEmpty(sources);
        foreach (var sourcePath in sources)
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var identifier in forbiddenIdentifiers)
                Assert.True(
                    !source.Contains(identifier, StringComparison.Ordinal),
                    $"Forbidden identifier '{identifier}' found in {sourcePath}.");
        }
    }

    [Fact]
    public void ProductionSourcesContainNoOwnershipRecordingCalls()
    {
        var forbiddenCalls = new[]
        {
            "AppendAsync",
            "ApplyAsync(",
            "ExecuteAuthorizedMutationAsync",
            "RecordRevertedAsync"
        };

        var sources = ProductionSourcesExcludingRestoration();
        Assert.NotEmpty(sources);
        foreach (var sourcePath in sources)
        {
            var source = File.ReadAllText(sourcePath);
            foreach (var call in forbiddenCalls)
                Assert.True(
                    !source.Contains(call, StringComparison.Ordinal),
                    $"Forbidden ownership-recording call '{call}' found in {sourcePath}.");
        }
    }

    [Fact]
    public void WorkerUsesOnlySecureV2CommandSurfaceWithoutOwnershipCommands()
    {
        var workerSource = ReadSource("src", "LibertyRoute.Service", "LibertyRouteWorker.cs");

        Assert.Contains("LibertyRoute.Network.v2", workerSource, StringComparison.Ordinal);
        Assert.Contains("SecureControlConnectionHandler", workerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Network.v1", workerSource, StringComparison.Ordinal);
        foreach (var legacy in new[] { "ReadLineAsync", "WriteLineAsync", "ToUpperInvariant", "JsonSerializer", "\"STATUS\"", "\"SNAPSHOT\"", "\"CONNECT\"", "\"DISCONNECT\"" })
            Assert.DoesNotContain(legacy, workerSource, StringComparison.Ordinal);

        foreach (var forbiddenCommand in new[] { "OWNERSHIP", "LEDGER", "EVIDENCE", "EXECUTE", "RESTORE", "APPLY" })
            Assert.DoesNotContain($"\"{forbiddenCommand}\"", workerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SecureControlProductionRegistrationsAreExactOnceSingletons()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        foreach (var type in new[]
        {
            typeof(TimeProvider), typeof(ControlServiceInstance), typeof(SecureControlPipeFactory),
            typeof(ControlCommandAuthorization), typeof(ControlRequestReplayGuard),
            typeof(IControlCommandDispatcher), typeof(SecureControlConnectionHandler)
        })
        {
            var descriptor = Assert.Single(services, item => item.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
        Assert.Equal(typeof(ControlCommandDispatcher),
            Assert.Single(services, item => item.ServiceType == typeof(IControlCommandDispatcher)).ImplementationType);
    }

    [Fact]
    public void HostedWorkerIsRegisteredAsTheOnlyHostedService()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        var hostedDescriptors = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();

        Assert.Single(hostedDescriptors);
        Assert.Equal(typeof(LibertyRouteWorker), hostedDescriptors[0].ImplementationType);
    }

    [Theory]
    [InlineData(RecoveryStartupReconciliationStatus.ManualRecoveryRequired)]
    [InlineData(RecoveryStartupReconciliationStatus.MalformedJournal)]
    [InlineData(RecoveryStartupReconciliationStatus.TerminalClearPending)]
    public async Task UnresolvedD1bRuntimeStartupNeverStartsListener(
        RecoveryStartupReconciliationStatus status)
    {
        var reconciler = new FixedStartupReconciler(status);
        var controller = new ConnectionController(new InertNetwork(), new InertJournal(), new InertEngine());
        var pipeFactory = new SecureControlPipeFactory(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        var handler = new SecureControlConnectionHandler(
            ControlServiceInstance.CreateTransient(), new ControlCommandAuthorization(),
            new ControlRequestReplayGuard(TimeProvider.System), new InertDispatcher(),
            TimeProvider.System,
            NullLogger<SecureControlConnectionHandler>.Instance);
        var worker = new LibertyRouteWorker(controller, pipeFactory, handler, reconciler,
            NullLogger<LibertyRouteWorker>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.StartAsync(CancellationToken.None));

        Assert.Equal(1, reconciler.Calls);
        Assert.Null(worker.ExecuteTask); // BackgroundService.StartAsync, and therefore ExecuteAsync/listener creation, was never reached.
    }

    [Theory]
    [InlineData(RecoveryStartupReconciliationStatus.NoJournal)]
    [InlineData(RecoveryStartupReconciliationStatus.ReconciledAndCleared)]
    public async Task ResolvedRuntimeStartupReachesListenerBoundary(
        RecoveryStartupReconciliationStatus status)
    {
        var worker = CreateRuntimeWorker(new ConnectionController(
            new InertNetwork(), new EmptyJournal(), new InertEngine()),
            new FixedStartupReconciler(status));
        await worker.StartAsync(CancellationToken.None);
        try { Assert.NotNull(worker.ExecuteTask); }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LegacyRuntimeRecoveryCompletesBeforeListenerBoundary()
    {
        var journal = new LegacyJournal();
        var network = new LegacyNetwork();
        var engine = new LegacyEngine();
        var worker = CreateRuntimeWorker(new ConnectionController(network, journal, engine),
            new FixedStartupReconciler(RecoveryStartupReconciliationStatus.LegacyRecoveryRequired));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, engine.StopCalls);
            Assert.Equal(1, network.VerifyCalls);
            Assert.Equal(1, journal.ClearCalls);
            Assert.NotNull(worker.ExecuteTask);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task LegacyRuntimeRecoveryFailureNeverStartsListener()
    {
        var engine = new LegacyEngine { ThrowOnStop = true };
        var worker = CreateRuntimeWorker(new ConnectionController(
            new LegacyNetwork(), new LegacyJournal(), engine),
            new FixedStartupReconciler(RecoveryStartupReconciliationStatus.LegacyRecoveryRequired));

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartAsync(CancellationToken.None));
        Assert.Equal(1, engine.StopCalls);
        Assert.Null(worker.ExecuteTask);
    }

    private static LibertyRouteWorker CreateRuntimeWorker(
        ConnectionController controller,
        IRecoveryStartupReconciler reconciler)
    {
        var pipeFactory = new SecureControlPipeFactory(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        var handler = new SecureControlConnectionHandler(
            ControlServiceInstance.CreateTransient(), new ControlCommandAuthorization(),
            new ControlRequestReplayGuard(TimeProvider.System), new InertDispatcher(),
            TimeProvider.System,
            NullLogger<SecureControlConnectionHandler>.Instance);
        return new LibertyRouteWorker(controller, pipeFactory, handler, reconciler,
            NullLogger<LibertyRouteWorker>.Instance);
    }

    [Fact]
    public void TransactionJournalIsRegisteredExactlyOnceAsSingletonFileImplementation()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(ITransactionJournal)).ToArray();

        Assert.Single(descriptors);
        Assert.Equal(ServiceLifetime.Singleton, descriptors[0].Lifetime);
        Assert.NotNull(descriptors[0].ImplementationFactory);
    }

    [Fact]
    public void TestSeamCompositionMatchesProductionRegistrations()
    {
        var production = new ServiceCollection();
        production.AddLibertyRouteCoreServices();
        var test = new ServiceCollection();
        test.AddLibertyRouteCoreServicesForTests(Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Composition", Guid.NewGuid().ToString("N")));

        var productionServiceTypes = production.Select(descriptor => descriptor.ServiceType).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var testServiceTypes = test.Select(descriptor => descriptor.ServiceType).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();

        Assert.Equal(productionServiceTypes, testServiceTypes);

        // The only permitted difference is the ledger factory: the test seam roots
        // FileOwnershipLedger at a temporary directory.
        var productionLedger = Assert.Single(production, descriptor => descriptor.ServiceType == typeof(IOwnershipLedger));
        var testLedger = Assert.Single(test, descriptor => descriptor.ServiceType == typeof(IOwnershipLedger));
        Assert.Null(productionLedger.ImplementationType);
        Assert.Null(testLedger.ImplementationType);
        Assert.NotNull(productionLedger.ImplementationFactory);
        Assert.NotNull(testLedger.ImplementationFactory);
    }

    private sealed class FixedStartupReconciler(RecoveryStartupReconciliationStatus status)
        : IRecoveryStartupReconciler
    {
        internal int Calls { get; private set; }
        public Task<RecoveryStartupReconciliationResult> ReconcileAsync(CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new RecoveryStartupReconciliationResult(
                status, Guid.NewGuid(), Guid.NewGuid(), RecoveryPhase.ExecutionStarted,
                false, "test unresolved state"));
        }
    }

    private sealed class InertNetwork : INetworkStateManager
    {
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
    }

    private sealed class InertJournal : ITransactionJournal
    {
        public string JournalPath => "worker-runtime-test";
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
        public Task ClearAsync(Guid session, CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
    }

    private sealed class EmptyJournal : ITransactionJournal
    {
        public string JournalPath => "worker-empty-test";
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token) => Task.FromResult<NetworkTransaction?>(null);
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token) => throw new NotSupportedException();
        public Task ClearAsync(Guid session, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class LegacyJournal : ITransactionJournal
    {
        private NetworkTransaction? _transaction = new(Guid.NewGuid(), ConnectionState.RollbackRequired,
            DateTimeOffset.UnixEpoch, new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test",
                Array.Empty<AdapterState>(), Array.Empty<RouteState>(), Array.Empty<DnsInterfaceState>()),
            Array.Empty<OwnedNetworkChange>(), "legacy", null);
        public string JournalPath => "worker-legacy-test";
        internal int ClearCalls { get; private set; }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token) => Task.FromResult(_transaction);
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token)
        { _transaction = transaction; return Task.CompletedTask; }
        public Task ClearAsync(Guid session, CancellationToken token)
        { Assert.Equal(_transaction!.SessionId, session); ClearCalls++; _transaction = null; return Task.CompletedTask; }
    }

    private sealed class LegacyNetwork : INetworkStateManager
    {
        internal int VerifyCalls { get; private set; }
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken token) => throw new NotSupportedException();
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken token)
        { VerifyCalls++; return Task.CompletedTask; }
    }

    private sealed class LegacyEngine : IConnectionEngine
    {
        public string Id => "legacy";
        internal int StopCalls { get; private set; }
        internal bool ThrowOnStop { get; init; }
        public Task StartAsync(VpnServerConfig server, CancellationToken token) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken token)
        { StopCalls++; return ThrowOnStop ? throw new InvalidOperationException("legacy failure") : Task.CompletedTask; }
        public Task<bool> IsHealthyAsync(CancellationToken token) => Task.FromResult(true);
    }

    private sealed class InertEngine : IConnectionEngine
    {
        public string Id => "inert";
        public Task StartAsync(VpnServerConfig server, CancellationToken token) => throw new InvalidOperationException("native execution is forbidden");
        public Task StopAsync(CancellationToken token) => throw new InvalidOperationException("legacy recovery is forbidden");
        public Task<bool> IsHealthyAsync(CancellationToken token) => throw new InvalidOperationException("native execution is forbidden");
    }

    private sealed class InertDispatcher : IControlCommandDispatcher
    {
        public Task<ControlDispatchResult> DispatchAsync(ControlCallerIdentity caller,
            ControlRequestEnvelope request, CancellationToken token)
            => throw new InvalidOperationException("listener must remain unreachable");
    }
}
