using System.Reflection;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlledRestorationActivationAuthorityTests
{
    private sealed class TestTrigger : IControlledRestorationActivationTrigger
    {
        private readonly ControlledRestorationTriggerDecision _decision;

        public TestTrigger(bool authorized = true)
        {
            _decision = new ControlledRestorationTriggerDecision(
                authorized ? ControlledRestorationTriggerStatus.Authorized : ControlledRestorationTriggerStatus.Denied,
                authorized ? "Controlled test authorization." : "Controlled test denial.");
        }

        public int EvaluationCount { get; private set; }

        public ControlledRestorationTriggerDecision Evaluate(RestorationOrchestrationPreparation preparation)
        {
            EvaluationCount++;
            return _decision;
        }
    }

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
                "Fake only.",
                false));
        }
    }

    private sealed class TestProviderGate : IControlledRestorationProviderGate
    {
        private readonly RouteMutationProviderFactory _factory;

        public TestProviderGate(IRestorationMutationProvider provider)
            => _factory = new RouteMutationProviderFactory(() => provider);

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

    private sealed class TestExecutor : IRecordedMutationExecutor
    {
        public TestExecutor(Guid activeSessionId) => ActiveSessionId = activeSessionId;

        public Guid ActiveSessionId { get; }
        public int RevertedCallCount { get; private set; }

        public Task<RecordedMutationExecution> ExecuteAsync(
            AuthorizedRestorationRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RecordedMutationExecution> RecordRevertedAsync(
            Guid changeId,
            CancellationToken cancellationToken)
        {
            RevertedCallCount++;
            throw new NotSupportedException();
        }
    }

    private sealed class TestExecutorFactory : IRecordedMutationExecutorFactory
    {
        public int CreateCount { get; private set; }
        public List<TestExecutor> Executors { get; } = new();

        public IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider)
        {
            CreateCount++;
            var executor = new TestExecutor(activeSessionId);
            Executors.Add(executor);
            return executor;
        }
    }

    private sealed class TestOrchestrator : IRestorationExecutionOrchestrator
    {
        public required RestorationBatchExecution Result { get; init; }
        public int ExecuteCount { get; private set; }

        public Task<RestorationOrchestrationPreparation> PrepareAsync(
            DryRunRestorationResult dryRunResult,
            Guid activeTransactionId,
            Guid activeSessionId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RestorationBatchExecution> ExecutePreparedAsync(
            RestorationOrchestrationPreparation preparation,
            IRecordedMutationExecutor executor,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(Result);
        }
    }

    private static RestorationOrchestrationPreparation Preparation(
        Guid? sessionId = null,
        Guid? transactionId = null,
        string targetPrefix = "route",
        int operationCount = 2)
    {
        var session = sessionId ?? Guid.NewGuid();
        var transaction = transactionId ?? Guid.NewGuid();
        var operations = Enumerable.Range(1, operationCount)
            .Select(order => Operation($"{targetPrefix}-{order}", order))
            .ToArray();
        var evidence = operations.Select(operation => Evidence(operation, session)).ToArray();
        var dryRun = new DryRunRestorationResult(
            operations,
            new DryRunRestorationSummary(
                operations.Length,
                operations.Length,
                operations.Length,
                0,
                0,
                true,
                Array.Empty<string>()));
        var authorization = RestorationAuthorizationPolicy.AuthorizeBatch(dryRun, evidence, session);
        return new RestorationOrchestrationPreparation(
            session,
            RestorationExecutionPreparation.Prepare(authorization, transaction, session));
    }

    private static DryRunRestorationOperation Operation(string target, int order)
        => new(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            target,
            $"baseline-{target}",
            $"applied-{target}",
            "Test operation.",
            order,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

    private static OwnershipEvidence Evidence(DryRunRestorationOperation operation, Guid sessionId)
        => new(
            sessionId,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.TestFixture,
            true);

    private static RestorationExecutionPreparation ExecutionPreparation(
        IReadOnlyList<AuthorizedRestorationRequest> requests,
        IReadOnlyList<DryRunRestorationOperation>? rejected = null,
        IReadOnlyList<string>? blockers = null,
        bool canExecute = true)
    {
        var constructor = typeof(RestorationExecutionPreparation)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single();
        return (RestorationExecutionPreparation)constructor.Invoke(new object[]
        {
            requests,
            rejected ?? Array.Empty<DryRunRestorationOperation>(),
            blockers ?? Array.Empty<string>(),
            canExecute
        });
    }

    private static AuthorizedRestorationRequest CloneRequest(
        AuthorizedRestorationRequest request,
        Guid? transactionId = null,
        Guid? sessionId = null,
        string? operationIdentity = null,
        int? executionOrder = null,
        bool? automaticallyExecutable = null,
        string? targetIdentity = null)
    {
        var constructor = typeof(AuthorizedRestorationRequest)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(candidate => candidate.GetParameters().Length == 13);
        return (AuthorizedRestorationRequest)constructor.Invoke(new object[]
        {
            transactionId ?? request.TransactionId,
            sessionId ?? request.SessionId,
            operationIdentity ?? request.OperationIdentity,
            request.Category,
            request.Action,
            targetIdentity ?? request.TargetIdentity,
            request.OriginalValue,
            request.CurrentValue,
            request.IntendedRestorationValue,
            executionOrder ?? request.ExecutionOrder,
            request.AuthorizationEvidenceId,
            request.AuthorizationReason,
            automaticallyExecutable ?? request.AutomaticallyExecutable
        });
    }

    private static RestorationOrchestrationPreparation WithRequests(
        RestorationOrchestrationPreparation preparation,
        IReadOnlyList<AuthorizedRestorationRequest> requests,
        IReadOnlyList<DryRunRestorationOperation>? rejected = null,
        IReadOnlyList<string>? blockers = null,
        bool canExecute = true,
        Guid? wrapperSession = null)
        => new(
            wrapperSession ?? preparation.ActiveSessionId,
            ExecutionPreparation(requests, rejected, blockers, canExecute));

    private static ControlledRestorationActivationAuthority Authority(
        TestTrigger trigger,
        Action? onReserved = null)
        => new(trigger, onReserved);

    [Fact]
    public async Task NormalProductionTriggerAlwaysDenies()
    {
        var decision = await new ControlledRestorationActivationAuthority().AuthorizeAsync(
            Preparation(), CancellationToken.None);

        Assert.Equal(ControlledRestorationActivationStatus.DeniedTriggerUnavailable, decision.Status);
        Assert.Null(decision.Grant);
    }

    [Theory]
    [InlineData("empty-session")]
    [InlineData("missing-preparation")]
    [InlineData("blocked")]
    [InlineData("rejected")]
    [InlineData("blockers")]
    [InlineData("empty")]
    [InlineData("wrong-session")]
    [InlineData("non-automatic")]
    [InlineData("duplicate-identity")]
    [InlineData("duplicate-order")]
    [InlineData("non-positive-order")]
    [InlineData("malformed")]
    [InlineData("inconsistent-transaction")]
    [InlineData("non-deterministic-order")]
    public async Task MalformedOrUnsafePreparationIsDeniedBeforeTrigger(string scenario)
    {
        var valid = Preparation();
        var requests = valid.ExecutionPreparation.AuthorizedRequests.ToArray();
        var invalid = scenario switch
        {
            "empty-session" => new RestorationOrchestrationPreparation(Guid.Empty, valid.ExecutionPreparation),
            "missing-preparation" => new RestorationOrchestrationPreparation(valid.ActiveSessionId, null!),
            "blocked" => WithRequests(valid, requests, canExecute: false),
            "rejected" => WithRequests(valid, requests, rejected: new[] { Operation("rejected", 9) }),
            "blockers" => WithRequests(valid, requests, blockers: new[] { "blocked" }),
            "empty" => WithRequests(valid, Array.Empty<AuthorizedRestorationRequest>()),
            "wrong-session" => WithRequests(valid, requests, wrapperSession: Guid.NewGuid()),
            "non-automatic" => WithRequests(valid, new[] { CloneRequest(requests[0], automaticallyExecutable: false), requests[1] }),
            "duplicate-identity" => WithRequests(valid, new[] { requests[0], CloneRequest(requests[1], operationIdentity: requests[0].OperationIdentity) }),
            "duplicate-order" => WithRequests(valid, new[] { requests[0], CloneRequest(requests[1], executionOrder: requests[0].ExecutionOrder) }),
            "non-positive-order" => WithRequests(valid, new[] { CloneRequest(requests[0], executionOrder: 0), requests[1] }),
            "malformed" => WithRequests(valid, new[] { CloneRequest(requests[0], targetIdentity: " "), requests[1] }),
            "inconsistent-transaction" => WithRequests(valid, new[] { requests[0], CloneRequest(requests[1], transactionId: Guid.NewGuid()) }),
            "non-deterministic-order" => WithRequests(valid, requests.Reverse().ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var trigger = new TestTrigger();

        var decision = await Authority(trigger).AuthorizeAsync(invalid, CancellationToken.None);

        Assert.Equal(ControlledRestorationActivationStatus.DeniedInvalidPreparation, decision.Status);
        Assert.Null(decision.Grant);
        Assert.Equal(0, trigger.EvaluationCount);
    }

    [Fact]
    public void FingerprintIsDeterministicAndBoundToExactSessionOperationsAndOrder()
    {
        var preparation = Preparation();
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(preparation, out var one, out _));
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(preparation, out var two, out _));

        var differentSession = Preparation(targetPrefix: "same");
        var differentOperation = Preparation(preparation.ActiveSessionId, targetPrefix: "different");
        var reorderedMetadata = WithRequests(
            preparation,
            preparation.ExecutionPreparation.AuthorizedRequests
                .Select((request, index) => CloneRequest(request, executionOrder: (index + 1) * 10))
                .ToArray());
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(differentSession, out var sessionFingerprint, out _));
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(differentOperation, out var operationFingerprint, out _));
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(reorderedMetadata, out var orderFingerprint, out _));

        Assert.Equal(one, two);
        Assert.NotEqual(one, sessionFingerprint);
        Assert.NotEqual(one, operationFingerprint);
        Assert.NotEqual(one, orderFingerprint);
    }

    [Fact]
    public void TransactionIdBindsExecutionEnvelopeButSessionRemainsOwnershipScope()
    {
        var session = Guid.NewGuid();
        var one = Preparation(session, Guid.NewGuid(), "same");
        var two = Preparation(session, Guid.NewGuid(), "same");
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(one, out var first, out _));
        Assert.True(ControlledRestorationPreparationFingerprint.TryCreate(two, out var second, out _));

        Assert.Equal(session, one.ActiveSessionId);
        Assert.Equal(session, two.ActiveSessionId);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ConcurrentIssuanceProducesExactlyOneOutstandingGrant()
    {
        var preparation = Preparation();
        var trigger = new TestTrigger();
        var authority = Authority(trigger);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => authority.AuthorizeAsync(preparation, CancellationToken.None)));

        var authorized = Assert.Single(decisions, decision => decision.Status == ControlledRestorationActivationStatus.Authorized);
        Assert.Equal(19, decisions.Count(decision => decision.Status == ControlledRestorationActivationStatus.DeniedOutstandingGrant));
        authorized.Grant!.Dispose();
    }

    [Fact]
    public async Task PreCancelledIssuanceProducesNothingAndDoesNotEvaluateTrigger()
    {
        var trigger = new TestTrigger();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Authority(trigger).AuthorizeAsync(Preparation(), cancellation.Token));

        Assert.Equal(0, trigger.EvaluationCount);
    }

    [Fact]
    public async Task DisposingUnconsumedGrantPermitsFreshIssuance()
    {
        var preparation = Preparation();
        var trigger = new TestTrigger();
        var authority = Authority(trigger);
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        first.Grant!.Dispose();
        var second = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationActivationStatus.Authorized, second.Status);
        Assert.Equal(2, trigger.EvaluationCount);
        second.Grant!.Dispose();
    }

    [Fact]
    public async Task CancellationAfterReservationLeavesNoOutstandingGrant()
    {
        var preparation = Preparation();
        var trigger = new TestTrigger();
        using var cancellation = new CancellationTokenSource();
        var authority = Authority(trigger, cancellation.Cancel);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            authority.AuthorizeAsync(preparation, cancellation.Token));

        var fresh = await Authority(trigger).AuthorizeAsync(preparation, CancellationToken.None);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task WrongPreparationBurnsGrantAndPermitsFreshIssuance()
    {
        var preparation = Preparation();
        var wrong = Preparation(preparation.ActiveSessionId, targetPrefix: "wrong");
        var trigger = new TestTrigger();
        var authority = Authority(trigger);
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        var consumption = first.Grant!.Consume(wrong, CancellationToken.None);
        var repeated = first.Grant.Consume(preparation, CancellationToken.None);
        var fresh = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationGrantConsumptionStatus.RejectedPreparationMismatch, consumption.Status);
        Assert.Equal(ControlledRestorationGrantConsumptionStatus.RejectedAlreadyTerminal, repeated.Status);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task WrongSessionBurnsGrantAndPermitsFreshIssuance()
    {
        var preparation = Preparation();
        var wrong = new RestorationOrchestrationPreparation(Guid.NewGuid(), preparation.ExecutionPreparation);
        var trigger = new TestTrigger();
        var authority = Authority(trigger);
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        var consumption = first.Grant!.Consume(wrong, CancellationToken.None);
        var fresh = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationGrantConsumptionStatus.RejectedPreparationMismatch, consumption.Status);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task CancelledConsumptionBurnsGrantAndPermitsFreshIssuance()
    {
        var preparation = Preparation();
        var trigger = new TestTrigger();
        var authority = Authority(trigger);
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => first.Grant!.Consume(preparation, cancellation.Token));
        var fresh = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task SuccessfulConsumptionReleasesReservationAndOldGrantStaysTerminal()
    {
        var preparation = Preparation();
        var authority = Authority(new TestTrigger());
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        var consumed = first.Grant!.Consume(preparation, CancellationToken.None);
        var repeated = first.Grant.Consume(preparation, CancellationToken.None);
        var fresh = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.True(consumed.IsConsumed);
        Assert.Equal(ControlledRestorationGrantConsumptionStatus.RejectedAlreadyTerminal, repeated.Status);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task StaleGrantDisposalCannotRemoveNewerReservation()
    {
        var preparation = Preparation();
        var authority = Authority(new TestTrigger());
        var old = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        Assert.True(old.Grant!.Consume(preparation, CancellationToken.None).IsConsumed);
        var current = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        old.Grant.Dispose();
        var blocked = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(ControlledRestorationActivationStatus.DeniedOutstandingGrant, blocked.Status);
        current.Grant!.Dispose();
    }

    [Fact]
    public async Task ConcurrentDuplicateConsumptionPermitsAtMostOneSuccess()
    {
        var preparation = Preparation();
        var decision = await Authority(new TestTrigger()).AuthorizeAsync(preparation, CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            decision.Grant!.Consume(preparation, CancellationToken.None))));

        Assert.Equal(1, results.Count(result => result.IsConsumed));
        Assert.Equal(19, results.Count(result => result.Status == ControlledRestorationGrantConsumptionStatus.RejectedAlreadyTerminal));
    }

    [Fact]
    public async Task ConcurrentDisposeAndConsumeHasAtMostOneUsableTransitionAndAllowsFreshGrant()
    {
        var preparation = Preparation();
        var authority = Authority(new TestTrigger());
        var decision = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        var start = new ManualResetEventSlim(false);
        ControlledRestorationGrantConsumption? consumption = null;

        var consume = Task.Run(() =>
        {
            start.Wait();
            consumption = decision.Grant!.Consume(preparation, CancellationToken.None);
        });
        var dispose = Task.Run(() =>
        {
            start.Wait();
            decision.Grant!.Dispose();
        });
        start.Set();
        await Task.WhenAll(consume, dispose);

        Assert.True(consumption!.Status is ControlledRestorationGrantConsumptionStatus.Consumed or ControlledRestorationGrantConsumptionStatus.RejectedAlreadyTerminal);
        Assert.True(decision.Grant!.IsTerminal);
        var fresh = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        Assert.Equal(ControlledRestorationActivationStatus.Authorized, fresh.Status);
        fresh.Grant!.Dispose();
    }

    [Fact]
    public async Task FreshIssuanceReevaluatesIndependentTrigger()
    {
        var preparation = Preparation();
        var trigger = new TestTrigger();
        var authority = Authority(trigger);
        var first = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        first.Grant!.Dispose();

        var second = await authority.AuthorizeAsync(preparation, CancellationToken.None);

        Assert.Equal(2, trigger.EvaluationCount);
        second.Grant!.Dispose();
    }

    [Fact]
    public async Task ControlledFakeChainPreservesExactPhase4EResultWithoutRevertedRecording()
    {
        var preparation = Preparation(operationCount: 1);
        var authority = Authority(new TestTrigger());
        var decision = await authority.AuthorizeAsync(preparation, CancellationToken.None);
        Assert.True(decision.Grant!.Consume(preparation, CancellationToken.None).IsConsumed);

        var expected = new RestorationBatchExecution(
            RestorationBatchExecutionStatus.StoppedAfterUnsafeOutcome,
            Array.Empty<AuthorizedRestorationRequest>(),
            Array.Empty<RecordedMutationExecution>(),
            null,
            preparation.ExecutionPreparation.AuthorizedRequests,
            true,
            "Exact partial/manual-recovery result.");
        var provider = new FakeProvider();
        var gate = new TestProviderGate(provider);
        var executorFactory = new TestExecutorFactory();
        var orchestrator = new TestOrchestrator { Result = expected };
        var boundary = new ControlledWindowsRestorationExecutionBoundary(orchestrator, executorFactory, gate);

        var result = await boundary.ExecuteAsync(preparation, CancellationToken.None);

        Assert.Same(expected, result.BatchExecution);
        Assert.True(result.BatchExecution!.RequiresManualRecovery);
        Assert.Equal(1, gate.CallCount);
        Assert.Equal(1, executorFactory.CreateCount);
        Assert.Equal(1, orchestrator.ExecuteCount);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, Assert.Single(executorFactory.Executors).RevertedCallCount);
    }

    [Fact]
    public void AuthorityCapabilityAndGrantRemainInternalWithoutPublicIssuersOrSwitches()
    {
        Assert.False(typeof(ControlledRestorationActivationAuthority).IsPublic);
        Assert.False(typeof(ControlledRestorationActivationGrant).IsPublic);
        Assert.False(typeof(RestorationExecutionCapability).IsPublic);
        Assert.Empty(typeof(RestorationExecutionCapability).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var publicParameterTypes = typeof(ControlledRestorationActivationAuthority)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(IServiceProvider), publicParameterTypes);
        Assert.DoesNotContain(typeof(string), publicParameterTypes);
        Assert.DoesNotContain(typeof(bool), publicParameterTypes);
    }

    [Fact]
    public void ControlledTestCapabilityHasNoProductionCallSitesAndNoAuthorityBridge()
    {
        var productionSources = ProductionSources();
        Assert.DoesNotContain(productionSources, path => File.ReadAllText(path).Contains(
            "RestorationExecutionCapability.CreateForControlledTest(", StringComparison.Ordinal));

        var authoritySource = ReadSource(
            "src", "LibertyRoute.Restoration.Windows", "ControlledRestorationActivationAuthority.cs");
        Assert.DoesNotContain(nameof(RestorationExecutionCapability), authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", authoritySource, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", authoritySource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalDiAndProductionProjectsHaveNoAuthorityOrActivationPath()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(typeof(IControlledRestorationActivationAuthority)));
        Assert.Null(provider.GetService(typeof(ControlledRestorationActivationAuthority)));
        Assert.Null(provider.GetService(typeof(ControlledRestorationActivationGrant)));
        Assert.Null(provider.GetService(typeof(RestorationExecutionCapability)));

        var serviceProject = ReadSource("src", "LibertyRoute.Service", "LibertyRoute.Service.csproj");
        Assert.DoesNotContain("LibertyRoute.Restoration.Windows", serviceProject, StringComparison.Ordinal);

        var normalSources = ProductionSourcesExcludingRestorationWindows();
        Assert.DoesNotContain(normalSources, path => File.ReadAllText(path).Contains(
            nameof(ControlledRestorationActivationAuthority), StringComparison.Ordinal));
        Assert.DoesNotContain(normalSources, path => File.ReadAllText(path).Contains(
            nameof(ControlledWindowsRestorationExecutionBoundary), StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryManagerAndWorkerSurfaceRemainUnchanged()
    {
        var recoveryConstructor = typeof(RecoveryManager).GetConstructors().Single();
        Assert.Equal(new[] { typeof(ITransactionJournal) }, recoveryConstructor.GetParameters().Select(parameter => parameter.ParameterType));

        var worker = ReadSource("src", "LibertyRoute.Service", "LibertyRouteWorker.cs");
        foreach (var command in new[] { "\"STATUS\"", "\"SNAPSHOT\"", "\"CONNECT\"", "\"DISCONNECT\"" })
            Assert.Contains(command, worker, StringComparison.Ordinal);
        foreach (var command in new[] { "RESTORE", "APPLY", "EXECUTE", "ACTIVATE", "AUTHORITY" })
            Assert.DoesNotContain($"\"{command}\"", worker, StringComparison.Ordinal);
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
