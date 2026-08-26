using System.Reflection;
using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public void ServiceProjectReferencesRestorationButNeverRestorationWindows()
    {
        var csproj = ReadSource("src", "LibertyRoute.Service", "LibertyRoute.Service.csproj");
        Assert.Contains("LibertyRoute.Restoration.csproj", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("LibertyRoute.Restoration.Windows", csproj, StringComparison.Ordinal);
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
    public void ServiceAssemblyReferencesRestorationButNotRestorationWindows()
    {
        var referenced = typeof(LibertyRouteRegistration).Assembly
            .GetReferencedAssemblies()
            .Select(assemblyName => assemblyName.Name)
            .ToArray();

        Assert.Contains("LibertyRoute.Restoration", referenced);
        Assert.DoesNotContain("LibertyRoute.Restoration.Windows", referenced);
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
        Assert.Equal(typeof(FileOwnershipLedger), descriptors[0].ImplementationType);
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
    public void WorkerCommandSurfaceRemainsUnchangedWithoutOwnershipCommands()
    {
        var workerSource = ReadSource("src", "LibertyRoute.Service", "LibertyRouteWorker.cs");

        foreach (var command in new[] { "\"STATUS\"", "\"SNAPSHOT\"", "\"CONNECT\"", "\"DISCONNECT\"" })
            Assert.Contains(command, workerSource, StringComparison.Ordinal);

        foreach (var forbiddenCommand in new[] { "OWNERSHIP", "LEDGER", "EVIDENCE", "EXECUTE", "RESTORE", "APPLY" })
            Assert.DoesNotContain($"\"{forbiddenCommand}\"", workerSource, StringComparison.Ordinal);
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

    [Fact]
    public void TransactionJournalIsRegisteredExactlyOnceAsSingletonFileImplementation()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        var descriptors = services.Where(descriptor => descriptor.ServiceType == typeof(ITransactionJournal)).ToArray();

        Assert.Single(descriptors);
        Assert.Equal(ServiceLifetime.Singleton, descriptors[0].Lifetime);
        Assert.Equal(typeof(FileTransactionJournal), descriptors[0].ImplementationType);
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
        Assert.Equal(typeof(FileOwnershipLedger), productionLedger.ImplementationType);
        Assert.Null(testLedger.ImplementationType);
        Assert.NotNull(testLedger.ImplementationFactory);
    }
}
