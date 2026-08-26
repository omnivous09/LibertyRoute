using System.Reflection;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlledRestorationActivationHandoffTests
{
    private sealed class AuthorizedTrigger : IControlledRestorationActivationTrigger
    {
        public ControlledRestorationTriggerDecision Evaluate(RestorationOrchestrationPreparation preparation)
            => new(ControlledRestorationTriggerStatus.Authorized, "Independent controlled test authorization.");
    }

    private sealed class FakeProvider : IRestorationMutationProvider
    {
        public int CallCount { get; private set; }

        public Task<RestorationMutationResult> ApplyAsync(
            AuthorizedRestorationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Live mutation is forbidden in this test.");
        }
    }

    private sealed class CapturingProviderFactory : IRestorationMutationProviderFactory
    {
        private readonly Func<RestorationExecutionPreflight> _create;

        public CapturingProviderFactory(Func<RestorationExecutionPreflight> create) => _create = create;

        public int CallCount { get; private set; }
        public List<RestorationExecutionCapability?> Capabilities { get; } = new();

        public RestorationExecutionPreflight Create(
            RestorationExecutionPreparation preparation,
            Guid activeSessionId,
            RestorationExecutionCapability? capability)
        {
            CallCount++;
            Capabilities.Add(capability);
            return _create();
        }
    }

    private sealed class FakeExecutor : IRecordedMutationExecutor
    {
        public FakeExecutor(Guid activeSessionId) => ActiveSessionId = activeSessionId;
        public Guid ActiveSessionId { get; }
        public Task<RecordedMutationExecution> ExecuteAsync(
            AuthorizedRestorationRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<RecordedMutationExecution> RecordRevertedAsync(
            Guid changeId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Reverted recording is forbidden in this test.");
    }

    private sealed class FakeExecutorFactory : IRecordedMutationExecutorFactory
    {
        public int CallCount { get; private set; }
        public IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider)
        {
            CallCount++;
            return new FakeExecutor(activeSessionId);
        }
    }

    private sealed class ExactResultOrchestrator : IRestorationExecutionOrchestrator
    {
        public required RestorationBatchExecution Result { get; init; }
        public int CallCount { get; private set; }
        public Task<RestorationOrchestrationPreparation> PrepareAsync(
            DryRunRestorationResult dryRunResult, Guid activeTransactionId, Guid activeSessionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RestorationBatchExecution> ExecutePreparedAsync(
            RestorationOrchestrationPreparation preparation, IRecordedMutationExecutor executor,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private static RestorationOrchestrationPreparation Preparation(Guid? sessionId = null)
    {
        var session = sessionId ?? Guid.NewGuid();
        var operation = new DryRunRestorationOperation(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            "route-handoff",
            "baseline",
            "applied",
            "Controlled test operation.",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);
        var evidence = new OwnershipEvidence(
            session,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.TestFixture,
            true);
        var dryRun = new DryRunRestorationResult(
            new[] { operation },
            new DryRunRestorationSummary(1, 1, 1, 0, 0, true, Array.Empty<string>()));
        var authorization = RestorationAuthorizationPolicy.AuthorizeBatch(dryRun, new[] { evidence }, session);
        return new RestorationOrchestrationPreparation(
            session,
            RestorationExecutionPreparation.Prepare(authorization, Guid.NewGuid(), session));
    }

    private static async Task<ControlledRestorationActivationGrant> GrantAsync(
        RestorationOrchestrationPreparation preparation)
    {
        var decision = await new ControlledRestorationActivationAuthority(new AuthorizedTrigger())
            .AuthorizeAsync(preparation, CancellationToken.None);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, decision.Status);
        return Assert.IsType<ControlledRestorationActivationGrant>(decision.Grant);
    }

    [Fact]
    public async Task ExactGrantCreatesTransientCapabilityAndReturnsProviderOnce()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        var provider = new FakeProvider();
        var factory = new CapturingProviderFactory(() => new(
            RestorationExecutionGateStatus.Enabled, "Fake gate accepted.", provider));
        var handoff = new ControlledRestorationActivationHandoff(grant, factory);

        var result = handoff.Create(preparation, CancellationToken.None);
        var replay = handoff.Create(preparation, CancellationToken.None);

        Assert.Equal(RestorationExecutionGateStatus.Enabled, result.Status);
        Assert.Same(provider, result.Provider);
        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability, replay.Status);
        Assert.Equal(1, factory.CallCount);
        Assert.True(Assert.Single(factory.Capabilities)!.IsValid);
        Assert.True(grant.IsTerminal);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task WrongPreparationBurnsGrantWithoutCreatingCapabilityOrProvider()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        var factory = new CapturingProviderFactory(() => throw new InvalidOperationException());
        var handoff = new ControlledRestorationActivationHandoff(grant, factory);

        var rejected = handoff.Create(Preparation(), CancellationToken.None);
        var replay = handoff.Create(preparation, CancellationToken.None);

        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability, rejected.Status);
        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability, replay.Status);
        Assert.Equal(0, factory.CallCount);
        Assert.True(grant.IsTerminal);
    }

    [Fact]
    public async Task ConcurrentHandoffAllowsAtMostOneProviderConstruction()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        var provider = new FakeProvider();
        var factory = new CapturingProviderFactory(() => new(
            RestorationExecutionGateStatus.Enabled, "Accepted.", provider));
        var handoff = new ControlledRestorationActivationHandoff(grant, factory);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => handoff.Create(preparation, CancellationToken.None))));

        Assert.Equal(1, results.Count(result => result.Status == RestorationExecutionGateStatus.Enabled));
        Assert.Equal(15, results.Count(result => result.Status == RestorationExecutionGateStatus.InvalidCapability));
        Assert.Equal(1, factory.CallCount);
    }

    [Fact]
    public async Task PreCancelledConsumptionBurnsGrantAndConstructsNothing()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        var factory = new CapturingProviderFactory(() => throw new InvalidOperationException());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handoff = new ControlledRestorationActivationHandoff(grant, factory);

        Assert.Throws<OperationCanceledException>(() => handoff.Create(preparation, cancellation.Token));
        var replay = handoff.Create(preparation, CancellationToken.None);

        Assert.True(grant.IsTerminal);
        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability, replay.Status);
        Assert.Equal(0, factory.CallCount);
    }

    [Fact]
    public async Task CancellationAfterConsumptionBurnsGrantBeforeCapabilityAndProvider()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        using var cancellation = new CancellationTokenSource();
        var factory = new CapturingProviderFactory(() => throw new InvalidOperationException());
        var handoff = new ControlledRestorationActivationHandoff(grant, factory, cancellation.Cancel);

        Assert.Throws<OperationCanceledException>(() => handoff.Create(preparation, cancellation.Token));

        Assert.True(grant.IsTerminal);
        Assert.Equal(0, factory.CallCount);
        Assert.Equal(
            RestorationExecutionGateStatus.InvalidCapability,
            handoff.Create(preparation, CancellationToken.None).Status);
    }

    [Fact]
    public async Task GateRejectionAndProviderFailureAreTerminal()
    {
        var preparation = Preparation();
        using var rejectedGrant = await GrantAsync(preparation);
        var rejectedFactory = new CapturingProviderFactory(() => new(
            RestorationExecutionGateStatus.BlockedByAuthorization, "Rejected.", null));
        var rejectedHandoff = new ControlledRestorationActivationHandoff(rejectedGrant, rejectedFactory);

        var rejected = rejectedHandoff.Create(preparation, CancellationToken.None);
        Assert.Equal(RestorationExecutionGateStatus.BlockedByAuthorization, rejected.Status);
        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability,
            rejectedHandoff.Create(preparation, CancellationToken.None).Status);

        using var failedGrant = await GrantAsync(preparation);
        var failedFactory = new CapturingProviderFactory(() => throw new InvalidOperationException("factory failed"));
        var failedHandoff = new ControlledRestorationActivationHandoff(failedGrant, failedFactory);
        var failed = failedHandoff.Create(preparation, CancellationToken.None);

        Assert.Equal(RestorationExecutionGateStatus.ProviderConstructionFailed, failed.Status);
        Assert.Null(failed.Provider);
        Assert.Equal(RestorationExecutionGateStatus.InvalidCapability,
            failedHandoff.Create(preparation, CancellationToken.None).Status);
    }

    [Fact]
    public async Task GrantBoundBoundaryPreservesExactPhase4EResultAndBurnsGrant()
    {
        var preparation = Preparation();
        using var grant = await GrantAsync(preparation);
        var provider = new FakeProvider();
        var providerFactory = new CapturingProviderFactory(() => new(
            RestorationExecutionGateStatus.Enabled, "Accepted.", provider));
        var expected = new RestorationBatchExecution(
            RestorationBatchExecutionStatus.CancelledAfterPartialExecution,
            preparation.ExecutionPreparation.AuthorizedRequests,
            Array.Empty<RecordedMutationExecution>(),
            null,
            Array.Empty<AuthorizedRestorationRequest>(),
            true,
            "Exact Phase 4E partial result.");
        var orchestrator = new ExactResultOrchestrator { Result = expected };
        var executorFactory = new FakeExecutorFactory();
        var boundary = new ControlledWindowsRestorationExecutionBoundary(
            orchestrator,
            executorFactory,
            new ControlledRestorationActivationHandoff(grant, providerFactory));

        var result = await boundary.ExecuteAsync(preparation, CancellationToken.None);

        Assert.Same(expected, result.BatchExecution);
        Assert.Equal(ControlledRestorationExecutionStatus.ExecutionReturned, result.Status);
        Assert.True(result.BatchExecution!.RequiresManualRecovery);
        Assert.True(grant.IsTerminal);
        Assert.Equal(1, providerFactory.CallCount);
        Assert.Equal(1, executorFactory.CallCount);
        Assert.Equal(1, orchestrator.CallCount);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void CapabilityAndHandoffRemainInternalAndCapabilityIsNotStoredOrReturned()
    {
        Assert.False(typeof(RestorationExecutionCapability).IsPublic);
        Assert.All(typeof(RestorationExecutionCapability).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.True(constructor.IsPrivate));
        var creation = typeof(RestorationExecutionCapability).GetMethod(
            "CreateForActivationHandoff", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(creation);
        Assert.True(creation!.IsAssembly);
        Assert.False(typeof(ControlledRestorationActivationHandoff).IsPublic);
        Assert.DoesNotContain(
            typeof(ControlledRestorationActivationHandoff).GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            field => field.FieldType == typeof(RestorationExecutionCapability));
        Assert.DoesNotContain(
            typeof(ControlledRestorationActivationHandoff).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            method => method.ReturnType == typeof(RestorationExecutionCapability));
    }

    [Fact]
    public async Task HandoffIsUnregisteredAndHasNoNormalProductionCallSites()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(typeof(ControlledRestorationActivationHandoff)));
        Assert.Null(provider.GetService(typeof(RestorationExecutionCapability)));

        var normalSources = ProductionSources()
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}LibertyRoute.Restoration.Windows{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));
        Assert.DoesNotContain(normalSources, path => File.ReadAllText(path).Contains(
            nameof(ControlledRestorationActivationHandoff), StringComparison.Ordinal));

        var windowsSources = ProductionSources()
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}LibertyRoute.Restoration.Windows{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Single(windowsSources, path => File.ReadAllText(path).Contains(
            "RestorationExecutionCapability.CreateForActivationHandoff(", StringComparison.Ordinal));
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

    private static string[] ProductionSources()
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                               $"{Path.DirectorySeparatorChar}LibertyRoute.Restoration.Tests{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
}
