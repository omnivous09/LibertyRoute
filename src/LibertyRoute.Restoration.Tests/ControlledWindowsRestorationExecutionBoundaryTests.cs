using System.Reflection;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlledWindowsRestorationExecutionBoundaryTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TransactionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class FakeProvider : IRestorationMutationProvider
    {
        public int CallCount { get; private set; }

        public Task<RestorationMutationResult> ApplyAsync(
            AuthorizedRestorationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new RestorationMutationResult(
                request.OperationIdentity,
                RestorationMutationState.Succeeded,
                "Fake success.",
                false));
        }
    }

    private sealed class FakeExecutor : IRecordedMutationExecutor
    {
        public FakeExecutor(Guid activeSessionId) => ActiveSessionId = activeSessionId;

        public Guid ActiveSessionId { get; }
        public int ExecuteCallCount { get; private set; }
        public int RevertedCallCount { get; private set; }

        public Task<RecordedMutationExecution> ExecuteAsync(
            AuthorizedRestorationRequest request,
            CancellationToken cancellationToken)
        {
            ExecuteCallCount++;
            return Task.FromResult(new RecordedMutationExecution(
                request.OperationIdentity,
                Guid.NewGuid(),
                request.SessionId,
                RecordedMutationOutcome.ExecutedAndApplied,
                null,
                true,
                true,
                false,
                "Fake recorded success."));
        }

        public Task<RecordedMutationExecution> RecordRevertedAsync(
            Guid changeId,
            CancellationToken cancellationToken)
        {
            RevertedCallCount++;
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingExecutorFactory : IRecordedMutationExecutorFactory
    {
        private readonly Action? _onCreate;

        public CapturingExecutorFactory(Action? onCreate = null) => _onCreate = onCreate;

        public Exception? CreateException { get; init; }
        public int CreateCount { get; private set; }
        public List<Guid> SessionIds { get; } = new();
        public List<IRestorationMutationProvider> Providers { get; } = new();
        public List<FakeExecutor> Executors { get; } = new();

        public IRecordedMutationExecutor Create(
            Guid activeSessionId,
            IRestorationMutationProvider provider)
        {
            CreateCount++;
            SessionIds.Add(activeSessionId);
            Providers.Add(provider);
            _onCreate?.Invoke();
            if (CreateException is not null)
                throw CreateException;

            var executor = new FakeExecutor(activeSessionId);
            Executors.Add(executor);
            return executor;
        }
    }

    private sealed class CapturingOrchestrator : IRestorationExecutionOrchestrator
    {
        public required RestorationBatchExecution ResultToReturn { get; init; }
        public int PrepareCallCount { get; private set; }
        public int ExecuteCallCount { get; private set; }
        public List<RestorationOrchestrationPreparation> Preparations { get; } = new();
        public List<IRecordedMutationExecutor> Executors { get; } = new();

        public Task<RestorationOrchestrationPreparation> PrepareAsync(
            DryRunRestorationResult dryRunResult,
            Guid activeTransactionId,
            Guid activeSessionId,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            throw new NotSupportedException();
        }

        public Task<RestorationBatchExecution> ExecutePreparedAsync(
            RestorationOrchestrationPreparation preparation,
            IRecordedMutationExecutor executor,
            CancellationToken cancellationToken)
        {
            ExecuteCallCount++;
            Preparations.Add(preparation);
            Executors.Add(executor);
            return Task.FromResult(ResultToReturn);
        }
    }

    private sealed class CountingGate : IControlledRestorationProviderGate
    {
        private readonly Func<RestorationExecutionPreflight> _create;

        public CountingGate(Func<RestorationExecutionPreflight> create)
            => _create = create;

        public int CallCount { get; private set; }

        public RestorationExecutionPreflight Create(
            RestorationOrchestrationPreparation preparation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return _create();
        }
    }

    private sealed class Phase3E4FakeProviderGate : IControlledRestorationProviderGate
    {
        private readonly RouteMutationProviderFactory _factory;

        public Phase3E4FakeProviderGate(Func<IRestorationMutationProvider> providerFactory)
            => _factory = new RouteMutationProviderFactory(providerFactory);

        public int CallCount { get; private set; }

        public RestorationExecutionPreflight Create(
            RestorationOrchestrationPreparation preparation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return _factory.Create(
                preparation.ExecutionPreparation,
                preparation.ActiveSessionId,
                RestorationExecutionCapability.CreateForControlledTest());
        }
    }

    private static RestorationOrchestrationPreparation Preparation()
    {
        var operation = new DryRunRestorationOperation(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            "route-a",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1",
            "<absent>",
            "Test operation.",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);
        var evidence = new OwnershipEvidence(
            SessionId,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.TestFixture,
            true);
        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, SessionId);
        var batch = RestorationAuthorizationPolicy.AuthorizeBatch(
            new DryRunRestorationResult(
                new[] { operation },
                new DryRunRestorationSummary(1, 1, 1, 0, 0, true, Array.Empty<string>())),
            new[] { evidence },
            SessionId);
        Assert.Equal(OperationAuthorizationStatus.Authorized, authorization.Status);

        return new RestorationOrchestrationPreparation(
            SessionId,
            RestorationExecutionPreparation.Prepare(batch, TransactionId, SessionId));
    }

    private static RestorationBatchExecution BatchResult(
        RestorationBatchExecutionStatus status = RestorationBatchExecutionStatus.Completed,
        bool requiresManualRecovery = false)
        => new(
            status,
            Array.Empty<AuthorizedRestorationRequest>(),
            Array.Empty<RecordedMutationExecution>(),
            null,
            Array.Empty<AuthorizedRestorationRequest>(),
            requiresManualRecovery,
            "Exact Phase 4E result.");

    private static ControlledWindowsRestorationExecutionBoundary Boundary(
        CapturingOrchestrator orchestrator,
        CapturingExecutorFactory executorFactory,
        IControlledRestorationProviderGate gate)
        => new(orchestrator, executorFactory, gate);

    [Fact]
    public async Task ProductionConstructorIsDisabledWithoutCapabilityAndConstructsNothing()
    {
        var batch = BatchResult();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = batch };
        var executorFactory = new CapturingExecutorFactory();
        var boundary = new ControlledWindowsRestorationExecutionBoundary(orchestrator, executorFactory);

        var result = await boundary.ExecuteAsync(Preparation(), CancellationToken.None);

        Assert.Equal(ControlledRestorationExecutionStatus.GateRejected, result.Status);
        Assert.Equal(RestorationExecutionGateStatus.Disabled, result.GateStatus);
        Assert.Null(result.BatchExecution);
        Assert.Equal(0, executorFactory.CreateCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public void CapabilityUnavailableGateNeverConstructsProviderOrNative()
    {
        var providerConstructionCount = 0;
        var factory = new RouteMutationProviderFactory(() =>
        {
            providerConstructionCount++;
            return new FakeProvider();
        });
        var gate = new CapabilityUnavailableRestorationProviderGate(factory);

        var result = gate.Create(Preparation(), CancellationToken.None);

        Assert.Equal(RestorationExecutionGateStatus.Disabled, result.Status);
        Assert.Null(result.Provider);
        Assert.Equal(0, providerConstructionCount);
    }

    [Fact]
    public async Task PreGateCancellationPropagatesWithoutGateExecutorOrOrchestratorCalls()
    {
        var gate = new CountingGate(() => throw new InvalidOperationException("Gate must not run."));
        var executorFactory = new CapturingExecutorFactory();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Boundary(orchestrator, executorFactory, gate).ExecuteAsync(Preparation(), cancellation.Token));

        Assert.Equal(0, gate.CallCount);
        Assert.Equal(0, executorFactory.CreateCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public async Task GateRejectionCreatesNoExecutorAndDoesNotInvokeOrchestrator()
    {
        var gate = new CountingGate(() => new RestorationExecutionPreflight(
            RestorationExecutionGateStatus.BlockedByBatch,
            "Blocked.",
            null));
        var executorFactory = new CapturingExecutorFactory();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };

        var result = await Boundary(orchestrator, executorFactory, gate).ExecuteAsync(
            Preparation(), CancellationToken.None);

        Assert.Equal(ControlledRestorationExecutionStatus.GateRejected, result.Status);
        Assert.Equal(RestorationExecutionGateStatus.BlockedByBatch, result.GateStatus);
        Assert.Null(result.BatchExecution);
        Assert.Equal(0, executorFactory.CreateCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public async Task ControlledChainUsesExactSessionProviderExecutorAndBatchResult()
    {
        var provider = new FakeProvider();
        var gate = new Phase3E4FakeProviderGate(() => provider);
        var executorFactory = new CapturingExecutorFactory();
        var expectedBatch = BatchResult();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = expectedBatch };
        var preparation = Preparation();

        var result = await Boundary(orchestrator, executorFactory, gate).ExecuteAsync(
            preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationExecutionStatus.ExecutionReturned, result.Status);
        Assert.Equal(RestorationExecutionGateStatus.Enabled, result.GateStatus);
        Assert.Same(expectedBatch, result.BatchExecution);
        Assert.Equal(1, gate.CallCount);
        Assert.Equal(1, executorFactory.CreateCount);
        Assert.Equal(SessionId, Assert.Single(executorFactory.SessionIds));
        Assert.Same(provider, Assert.Single(executorFactory.Providers));
        Assert.Same(preparation, Assert.Single(orchestrator.Preparations));
        Assert.Same(Assert.Single(executorFactory.Executors), Assert.Single(orchestrator.Executors));
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, executorFactory.Executors[0].RevertedCallCount);
    }

    [Theory]
    [InlineData(RestorationBatchExecutionStatus.StoppedAfterUnsafeOutcome, false)]
    [InlineData(RestorationBatchExecutionStatus.ExecutorThrewAfterAttempt, true)]
    [InlineData(RestorationBatchExecutionStatus.CancelledAfterExecutionAttempt, true)]
    [InlineData(RestorationBatchExecutionStatus.CancelledAfterPartialExecution, true)]
    public async Task Phase4EResultIsPreservedExactly(
        RestorationBatchExecutionStatus status,
        bool requiresManualRecovery)
    {
        var expected = BatchResult(status, requiresManualRecovery);
        var orchestrator = new CapturingOrchestrator { ResultToReturn = expected };
        var executorFactory = new CapturingExecutorFactory();
        var gate = new Phase3E4FakeProviderGate(() => new FakeProvider());

        var result = await Boundary(orchestrator, executorFactory, gate).ExecuteAsync(
            Preparation(), CancellationToken.None);

        Assert.Same(expected, result.BatchExecution);
        Assert.Equal(requiresManualRecovery, result.BatchExecution!.RequiresManualRecovery);
    }

    [Fact]
    public async Task ExecutorCreationFailureIsStructuredAndMakesZeroMutationCalls()
    {
        var provider = new FakeProvider();
        var executorFactory = new CapturingExecutorFactory
        {
            CreateException = new InvalidOperationException("factory failed")
        };
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };

        var result = await Boundary(
            orchestrator,
            executorFactory,
            new Phase3E4FakeProviderGate(() => provider)).ExecuteAsync(
                Preparation(), CancellationToken.None);

        Assert.Equal(ControlledRestorationExecutionStatus.ExecutorCreationFailed, result.Status);
        Assert.Equal(RestorationExecutionGateStatus.Enabled, result.GateStatus);
        Assert.Null(result.BatchExecution);
        Assert.Equal(1, executorFactory.CreateCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public async Task CancellationAfterProviderConstructionCreatesNoExecutorOrMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider();
        var gate = new Phase3E4FakeProviderGate(() =>
        {
            cancellation.Cancel();
            return provider;
        });
        var executorFactory = new CapturingExecutorFactory();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Boundary(orchestrator, executorFactory, gate).ExecuteAsync(Preparation(), cancellation.Token));

        Assert.Equal(1, gate.CallCount);
        Assert.Equal(0, executorFactory.CreateCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public async Task CancellationAfterExecutorCreationDoesNotBeginOrchestrationOrMutation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider();
        var executorFactory = new CapturingExecutorFactory(cancellation.Cancel);
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Boundary(
                orchestrator,
                executorFactory,
                new Phase3E4FakeProviderGate(() => provider)).ExecuteAsync(
                    Preparation(), cancellation.Token));

        Assert.Equal(1, executorFactory.CreateCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public async Task ProviderAndExecutorAreShortLivedPerInvocation()
    {
        var providers = new List<FakeProvider>();
        var gate = new Phase3E4FakeProviderGate(() =>
        {
            var provider = new FakeProvider();
            providers.Add(provider);
            return provider;
        });
        var executorFactory = new CapturingExecutorFactory();
        var orchestrator = new CapturingOrchestrator { ResultToReturn = BatchResult() };
        var boundary = Boundary(orchestrator, executorFactory, gate);

        await boundary.ExecuteAsync(Preparation(), CancellationToken.None);
        await boundary.ExecuteAsync(Preparation(), CancellationToken.None);

        Assert.Equal(2, providers.Count);
        Assert.NotSame(providers[0], providers[1]);
        Assert.Equal(2, executorFactory.Executors.Count);
        Assert.NotSame(executorFactory.Executors[0], executorFactory.Executors[1]);
        Assert.Equal(2, orchestrator.ExecuteCallCount);
    }

    [Fact]
    public void BoundaryHasNoCapabilityConfigurationEnvironmentOrServiceProviderInput()
    {
        var publicParameterTypes = typeof(ControlledWindowsRestorationExecutionBoundary)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Concat(typeof(IControlledWindowsRestorationExecutionBoundary).GetMethods().SelectMany(method => method.GetParameters()))
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(typeof(RestorationExecutionCapability), publicParameterTypes);
        Assert.DoesNotContain(typeof(IServiceProvider), publicParameterTypes);
        Assert.DoesNotContain(typeof(string), publicParameterTypes);
        Assert.DoesNotContain(typeof(bool), publicParameterTypes);

        var source = ReadSource(
            "src",
            "LibertyRoute.Restoration.Windows",
            "ControlledWindowsRestorationExecutionBoundary.cs");
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlledTestCapabilityHasZeroProductionCallSites()
    {
        var productionSources = ProductionSources();
        Assert.DoesNotContain(
            productionSources,
            path => File.ReadAllText(path).Contains(
                "RestorationExecutionCapability.CreateForControlledTest(",
                StringComparison.Ordinal));
    }

    [Fact]
    public void NormalProductionProjectsHaveNoBoundaryInvocationOrReference()
    {
        var productionSources = ProductionSourcesExcludingRestorationWindows();
        Assert.DoesNotContain(
            productionSources,
            path => File.ReadAllText(path).Contains(
                nameof(ControlledWindowsRestorationExecutionBoundary),
                StringComparison.Ordinal));

        var serviceProject = ReadSource("src", "LibertyRoute.Service", "LibertyRoute.Service.csproj");
        Assert.Contains("LibertyRoute.Restoration.Windows", serviceProject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalServiceDiCannotResolveBoundaryProviderNativeOrCapability()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        await using var provider = services.BuildServiceProvider();

        Assert.Null(provider.GetService<IControlledWindowsRestorationExecutionBoundary>());
        Assert.Null(provider.GetService<ControlledWindowsRestorationExecutionBoundary>());
        Assert.Null(provider.GetService<IRestorationMutationProvider>());
        Assert.Null(provider.GetService<IRouteMutationNative>());
        Assert.Null(provider.GetService(typeof(RestorationExecutionCapability)));
        Assert.Null(provider.GetService(typeof(WindowsRouteMutationNative)));
    }

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

    private static string[] ProductionSources()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}LibertyRoute.Restoration.Tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

    private static string[] ProductionSourcesExcludingRestorationWindows()
        => ProductionSources()
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}LibertyRoute.Restoration.Windows{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
}
