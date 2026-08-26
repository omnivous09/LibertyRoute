using System.Reflection;
using LibertyRoute.Core;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RecordedMutationExecutorTests : IAsyncLifetime
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TransactionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string RouteValue = "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1";

    private string _root = string.Empty;

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.RecordedExecutor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }

        await Task.CompletedTask;
    }

    private FileOwnershipLedger NewLedger() => new(_root);

    private sealed record RequestFixture(AuthorizedRestorationRequest Request, DryRunRestorationOperation Operation);

    private static RequestFixture Fixture(Guid sessionId, Guid transactionId, string target = "route-10.0.0.0/24")
    {
        var operation = new DryRunRestorationOperation(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            target,
            RouteValue,
            "<absent>",
            "baseline route missing",
            1,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

        var evidence = new OwnershipEvidence(
            sessionId,
            operation.Category,
            operation.TargetIdentity,
            operation.OriginalValue,
            operation.CurrentValue,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder,
            OwnershipEvidenceSource.MutationLedger,
            true);

        var authorization = RestorationAuthorizationPolicy.Authorize(operation, new[] { evidence }, sessionId);
        var request = AuthorizedRestorationRequest.Create(operation, authorization, transactionId, sessionId);
        return new RequestFixture(request, operation);
    }

    private sealed class FakeProvider : IRestorationMutationProvider
    {
        private readonly Func<CancellationToken, Task<RestorationMutationResult>> _handler;
        private int _callCount;

        public FakeProvider(Func<CancellationToken, Task<RestorationMutationResult>> handler)
            => _handler = handler;

        public int CallCount => Volatile.Read(ref _callCount);
        public List<AuthorizedRestorationRequest> Requests { get; } = new();

        public async Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Requests.Add(request);
            return await _handler(cancellationToken);
        }

        public static FakeProvider Returning(RestorationMutationState state)
            => new(_ => Task.FromResult(new RestorationMutationResult("synthetic-operation", state, $"synthetic {state}", false)));

        public static FakeProvider Throwing(Exception exception)
            => new(_ => throw exception);
    }

    private sealed class ScriptableLedger : IOwnershipLedger
    {
        private readonly FileOwnershipLedger _inner;

        public ScriptableLedger(string root) => _inner = new FileOwnershipLedger(root);

        public Func<PersistedOwnedChange, Exception?>? AppendInterceptor { get; set; }

        public async Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken)
        {
            if (AppendInterceptor?.Invoke(record) is { } failure)
                throw failure;

            await _inner.AppendAsync(record, cancellationToken);
        }

        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => _inner.ReadForSessionAsync(sessionId, cancellationToken);

        public Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => _inner.ClearSessionAsync(sessionId, cancellationToken);

        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken)
            => _inner.ExistsAsync(sessionId, changeId, cancellationToken);
    }

    [Fact]
    public void FactoryCreatesSessionBoundImmutableExecutor()
    {
        var executor = new RecordedMutationExecutorFactory(NewLedger()).Create(SessionId, FakeProvider.Returning(RestorationMutationState.Succeeded));
        Assert.Equal(SessionId, executor.ActiveSessionId);
    }

    [Fact]
    public void FactoryRejectsEmptySession()
    {
        var factory = new RecordedMutationExecutorFactory(NewLedger());
        Assert.Throws<ArgumentException>(() => factory.Create(Guid.Empty, FakeProvider.Returning(RestorationMutationState.Succeeded)));
    }

    [Fact]
    public void FactoryRejectsNullProvider()
    {
        var factory = new RecordedMutationExecutorFactory(NewLedger());
        Assert.Throws<ArgumentNullException>(() => factory.Create(SessionId, null!));
    }

    [Fact]
    public async Task ExecutorDelegatesThroughCoordinatorSucceededToApplied()
    {
        var ledger = NewLedger();
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, result.Outcome);
        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Applied, stored.Lifecycle);
    }

    [Fact]
    public async Task PlannedExistsBeforeProviderInvocation()
    {
        var ledger = NewLedger();
        PersistedOwnedChange[] observed = Array.Empty<PersistedOwnedChange>();
        var provider = new FakeProvider(async _ =>
        {
            observed = (await ledger.ReadForSessionAsync(SessionId, CancellationToken.None)).ToArray();
            return new RestorationMutationResult("synthetic", RestorationMutationState.Succeeded, "ok", false);
        });
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        var record = Assert.Single(observed);
        Assert.Equal(OwnedChangeLifecycle.Planned, record.Lifecycle);
    }

    [Theory]
    [InlineData(RestorationMutationState.Failed, RecordedMutationOutcome.MutationFailedRemainsPlanned)]
    [InlineData(RestorationMutationState.AlreadyRestored, RecordedMutationOutcome.AlreadyRestoredOwnershipNotClaimedRemainsPlanned)]
    public async Task NonSucceededStatesRemainPlanned(RestorationMutationState state, RecordedMutationOutcome expected)
    {
        var ledger = NewLedger();
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, FakeProvider.Returning(state));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        var stored = Assert.Single(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
        Assert.Equal(OwnedChangeLifecycle.Planned, stored.Lifecycle);
        Assert.False(stored.IsComplete);
    }

    [Fact]
    public async Task ProviderThrowRequiresManualRecovery()
    {
        var ledger = NewLedger();
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, FakeProvider.Throwing(new InvalidOperationException("boom")));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ProviderThrewMutationIndeterminateManualRecoveryRequired, result.Outcome);
        Assert.True(result.RequiresManualRecovery);
    }

    [Fact]
    public async Task AppliedPersistenceFailureRemainsVisible()
    {
        var ledger = new ScriptableLedger(_root)
        {
            AppendInterceptor = record => record.Lifecycle == OwnedChangeLifecycle.Applied ? new IOException("disk") : null
        };
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, FakeProvider.Returning(RestorationMutationState.Succeeded));
        var fixture = Fixture(SessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.MutationSucceededOwnershipRecordingFailed, result.Outcome);
        Assert.True(result.RequiresManualRecovery);
        Assert.False(result.AppliedPersisted);
    }

    [Fact]
    public async Task RepeatedExecutionAfterAppliedSkipsProvider()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        await executor.ExecuteAsync(fixture.Request, CancellationToken.None);
        var second = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.AlreadyAppliedNoProviderCall, second.Outcome);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task PreExistingPlannedBlocksProvider()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Failed);
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        await executor.ExecuteAsync(fixture.Request, CancellationToken.None);
        var second = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.ExistingPlannedBlockedManualRecoveryRequired, second.Outcome);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task RevertedBlocksProviderAndRecordRevertedIsMetadataOnly()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var executed = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);
        var reverted = await executor.RecordRevertedAsync(executed.ChangeId, CancellationToken.None);
        var reExecuted = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.RevertedRecordingSucceeded, reverted.Outcome);
        Assert.Equal(RecordedMutationOutcome.ExistingRevertedRejectedNoProviderCall, reExecuted.Outcome);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task ConcurrentDuplicateExecutionCallsProviderOnce()
    {
        var ledger = NewLedger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider(async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new RestorationMutationResult("synthetic", RestorationMutationState.Succeeded, "ok", false);
        });

        var factory = new RecordedMutationExecutorFactory(ledger);
        var first = factory.Create(SessionId, provider);
        var second = factory.Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var task1 = first.ExecuteAsync(fixture.Request, CancellationToken.None);
        await entered.Task;
        var task2 = second.ExecuteAsync(fixture.Request, CancellationToken.None);
        await Task.Delay(150);
        release.SetResult();

        var result1 = await task1;
        var result2 = await task2;

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, result1.Outcome);
        Assert.Equal(RecordedMutationOutcome.AlreadyAppliedNoProviderCall, result2.Outcome);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task CancellationSemanticsArePreserved()
    {
        var ledger = NewLedger();
        using var cts = new CancellationTokenSource();
        var provider = new FakeProvider(_ =>
        {
            cts.Cancel();
            return Task.FromResult(new RestorationMutationResult("synthetic", RestorationMutationState.Succeeded, "ok", false));
        });
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(SessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, cts.Token);

        Assert.Equal(RecordedMutationOutcome.ExecutedAndApplied, result.Outcome);
        Assert.True(result.AppliedPersisted);
    }

    [Fact]
    public async Task WrongSessionRequestDoesNotSideEffect()
    {
        var ledger = NewLedger();
        var provider = FakeProvider.Returning(RestorationMutationState.Succeeded);
        var executor = new RecordedMutationExecutorFactory(ledger).Create(SessionId, provider);
        var fixture = Fixture(OtherSessionId, TransactionId);

        var result = await executor.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(RecordedMutationOutcome.SessionMismatchRejectedNoSideEffects, result.Outcome);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await ledger.ReadForSessionAsync(SessionId, CancellationToken.None));
    }

    [Fact]
    public void FactoryDependsOnlyOnLedgerAndNoIServiceProviderParameter()
    {
        var ctor = typeof(RecordedMutationExecutorFactory).GetConstructors().Single();
        Assert.Single(ctor.GetParameters());
        Assert.Equal(typeof(IOwnershipLedger), ctor.GetParameters()[0].ParameterType);

        var create = typeof(IRecordedMutationExecutorFactory).GetMethods().Single(method => method.Name == nameof(IRecordedMutationExecutorFactory.Create));
        var parameters = create.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert.Equal(new[] { typeof(Guid), typeof(IRestorationMutationProvider) }, parameters);
        Assert.DoesNotContain(parameters, type => type == typeof(IServiceProvider));
    }

    [Fact]
    public void ExecutorAndFactoryExposeNoLiveToggleSwitches()
    {
        var forbiddenFieldTypes = new[] { typeof(string), typeof(bool) };
        Assert.DoesNotContain(typeof(RecordedMutationExecutorFactory).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), field => forbiddenFieldTypes.Contains(field.FieldType));
        Assert.DoesNotContain(typeof(RecordedMutationExecutor).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), field => field.Name.Contains("live", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutorEntryPointsAcceptNoRawStringsOrPlanningTypes()
    {
        var execute = typeof(IRecordedMutationExecutor).GetMethod(nameof(IRecordedMutationExecutor.ExecuteAsync))!;
        var reverted = typeof(IRecordedMutationExecutor).GetMethod(nameof(IRecordedMutationExecutor.RecordRevertedAsync))!;
        Assert.Equal(typeof(AuthorizedRestorationRequest), execute.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(execute.GetParameters(), parameter => parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(DryRunRestorationOperation) || parameter.ParameterType == typeof(RestorationPlan));
        Assert.Equal(typeof(Guid), reverted.GetParameters()[0].ParameterType);
    }
}