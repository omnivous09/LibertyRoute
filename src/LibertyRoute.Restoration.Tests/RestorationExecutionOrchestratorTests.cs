using System.Reflection;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class RestorationExecutionOrchestratorTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TransactionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private sealed class TestLedger : IOwnershipLedger
    {
        private readonly IReadOnlyList<PersistedOwnedChange> _records;

        public TestLedger(IReadOnlyList<PersistedOwnedChange>? records = null)
        {
            _records = records ?? Array.Empty<PersistedOwnedChange>();
        }

        public Exception? ReadException { get; init; }
        public int ReadCount { get; private set; }
        public Guid LastReadSessionId { get; private set; }

        public Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            LastReadSessionId = sessionId;
            return ReadException is null
                ? Task.FromResult(_records)
                : Task.FromException<IReadOnlyList<PersistedOwnedChange>>(ReadException);
        }

        public Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class TestExecutor : IRecordedMutationExecutor
    {
        private readonly Func<AuthorizedRestorationRequest, int, CancellationToken, Task<RecordedMutationExecution>> _execute;

        public TestExecutor(
            Guid activeSessionId,
            Func<AuthorizedRestorationRequest, int, CancellationToken, Task<RecordedMutationExecution>>? execute = null)
        {
            ActiveSessionId = activeSessionId;
            _execute = execute ?? ((request, _, _) => Task.FromResult(Execution(request, RecordedMutationOutcome.ExecutedAndApplied)));
        }

        public Guid ActiveSessionId { get; }
        public List<AuthorizedRestorationRequest> Calls { get; } = new();
        public int RevertedCallCount { get; private set; }

        public Task<RecordedMutationExecution> ExecuteAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return _execute(request, Calls.Count, cancellationToken);
        }

        public Task<RecordedMutationExecution> RecordRevertedAsync(Guid changeId, CancellationToken cancellationToken)
        {
            RevertedCallCount++;
            throw new NotSupportedException();
        }
    }

    private static DryRunRestorationOperation Operation(int order)
        => new(
            DryRunOperationCategory.Route,
            DryRunAction.RestoreBaseline,
            $"route-{order}",
            $"baseline-{order}",
            $"applied-{order}",
            "Ownership required.",
            order,
            true,
            true,
            DryRunSafetyState.SafeToPlan);

    private static PersistedOwnedChange Record(
        DryRunRestorationOperation operation,
        OwnedChangeLifecycle lifecycle = OwnedChangeLifecycle.Applied,
        Guid? sessionId = null,
        string? target = null,
        string? original = null,
        string? applied = null)
        => PersistedOwnedChange.Create(
            sessionId ?? SessionId,
            Guid.NewGuid(),
            operation.Category,
            target ?? operation.TargetIdentity,
            original ?? operation.OriginalValue,
            applied ?? operation.CurrentValue,
            DateTimeOffset.Parse("2026-08-26T00:00:00Z"),
            operation.ExecutionOrder,
            OwnershipEvidenceSource.MutationLedger,
            lifecycle);

    private static DryRunRestorationResult DryRun(params DryRunRestorationOperation[] operations)
        => new(
            operations,
            new DryRunRestorationSummary(
                operations.Length,
                operations.Length,
                operations.Length,
                0,
                0,
                operations.All(operation => operation.AutomaticExecutionAllowed),
                Array.Empty<string>()));

    private static RecordedMutationExecution Execution(
        AuthorizedRestorationRequest request,
        RecordedMutationOutcome outcome,
        bool requiresManualRecovery = false)
        => new(
            request.OperationIdentity,
            Guid.NewGuid(),
            request.SessionId,
            outcome,
            null,
            PlannedPersisted: outcome != RecordedMutationOutcome.PlannedWriteFailedMutationNotAttempted,
            AppliedPersisted: outcome is RecordedMutationOutcome.ExecutedAndApplied or RecordedMutationOutcome.AlreadyAppliedNoProviderCall,
            RequiresManualRecovery: requiresManualRecovery,
            outcome.ToString());

    private static async Task<RestorationOrchestrationPreparation> PrepareAsync(
        IReadOnlyList<DryRunRestorationOperation> operations,
        IReadOnlyList<PersistedOwnedChange> records)
        => await new RestorationExecutionOrchestrator(new TestLedger(records)).PrepareAsync(
            DryRun(operations.ToArray()),
            TransactionId,
            SessionId,
            CancellationToken.None);

    [Fact]
    public async Task MissingLedgerDoesNotCreateFalseOwnershipAuthorization()
    {
        var operation = Operation(1);
        var preparation = await PrepareAsync(new[] { operation }, Array.Empty<PersistedOwnedChange>());

        Assert.False(preparation.ExecutionPreparation.CanExecuteAutomatically);
        Assert.Empty(preparation.ExecutionPreparation.AuthorizedRequests);
        Assert.Single(preparation.ExecutionPreparation.RejectedOperations);
    }

    [Fact]
    public async Task CorruptLedgerFailureIsExplicitAndCannotExecute()
    {
        var ledger = new TestLedger { ReadException = new InvalidDataException("corrupt") };
        var orchestrator = new RestorationExecutionOrchestrator(ledger);
        var executor = new TestExecutor(SessionId);

        await Assert.ThrowsAsync<InvalidDataException>(() => orchestrator.PrepareAsync(
            DryRun(Operation(1)), TransactionId, SessionId, CancellationToken.None));

        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task PreparationReadsOnlyTheActiveSessionAndExactAppliedEvidenceAuthorizes()
    {
        var operation = Operation(1);
        var ledger = new TestLedger(new[] { Record(operation) });
        var preparation = await new RestorationExecutionOrchestrator(ledger).PrepareAsync(
            DryRun(operation), TransactionId, SessionId, CancellationToken.None);

        Assert.Equal(1, ledger.ReadCount);
        Assert.Equal(SessionId, ledger.LastReadSessionId);
        Assert.Equal(SessionId, preparation.ActiveSessionId);
        Assert.True(preparation.ExecutionPreparation.CanExecuteAutomatically);
        Assert.Single(preparation.ExecutionPreparation.AuthorizedRequests);
    }

    [Theory]
    [InlineData(OwnedChangeLifecycle.Planned)]
    [InlineData(OwnedChangeLifecycle.Reverted)]
    public async Task IncompleteLifecycleEvidenceIsDenied(OwnedChangeLifecycle lifecycle)
    {
        var operation = Operation(1);
        var preparation = await PrepareAsync(new[] { operation }, new[] { Record(operation, lifecycle) });

        Assert.False(preparation.ExecutionPreparation.CanExecuteAutomatically);
        Assert.Empty(preparation.ExecutionPreparation.AuthorizedRequests);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("original")]
    [InlineData("applied")]
    public async Task AlteredEvidenceIsDenied(string alteredField)
    {
        var operation = Operation(1);
        var record = Record(
            operation,
            target: alteredField == "target" ? "other-target" : null,
            original: alteredField == "original" ? "other-original" : null,
            applied: alteredField == "applied" ? "other-applied" : null);

        var preparation = await PrepareAsync(new[] { operation }, new[] { record });

        Assert.False(preparation.ExecutionPreparation.CanExecuteAutomatically);
        Assert.Empty(preparation.ExecutionPreparation.AuthorizedRequests);
    }

    [Fact]
    public async Task MixedBatchIsWhollyBlockedAndDeterministic()
    {
        var first = Operation(1);
        var second = Operation(2);
        var ledger = new TestLedger(new[] { Record(first) });
        var orchestrator = new RestorationExecutionOrchestrator(ledger);

        var one = await orchestrator.PrepareAsync(DryRun(first, second), TransactionId, SessionId, CancellationToken.None);
        var two = await orchestrator.PrepareAsync(DryRun(first, second), TransactionId, SessionId, CancellationToken.None);
        var executor = new TestExecutor(SessionId);
        var result = await orchestrator.ExecutePreparedAsync(one, executor, CancellationToken.None);

        Assert.False(one.ExecutionPreparation.CanExecuteAutomatically);
        Assert.Equal(one.ExecutionPreparation.AuthorizedRequests, two.ExecutionPreparation.AuthorizedRequests);
        Assert.Equal(one.ExecutionPreparation.BlockingReasons, two.ExecutionPreparation.BlockingReasons);
        Assert.Equal(RestorationBatchExecutionStatus.BlockedBeforeExecution, result.Status);
        Assert.Empty(executor.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EmptySessionOrTransactionIsRejected(bool emptySession, bool emptyTransaction)
    {
        var orchestrator = new RestorationExecutionOrchestrator(new TestLedger());

        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.PrepareAsync(
            DryRun(Operation(1)),
            emptyTransaction ? Guid.Empty : TransactionId,
            emptySession ? Guid.Empty : SessionId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ExecutorSessionMismatchBlocksWithoutCalls()
    {
        var operation = Operation(1);
        var preparation = await PrepareAsync(new[] { operation }, new[] { Record(operation) });
        var executor = new TestExecutor(Guid.NewGuid());

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.BlockedBeforeExecution, result.Status);
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task ExecutableBatchPreservesOrderAndAcceptsBothAppliedOutcomes()
    {
        var operations = new[] { Operation(1), Operation(2), Operation(3) };
        var preparation = await PrepareAsync(operations, operations.Select(operation => Record(operation)).ToArray());
        var executor = new TestExecutor(
            SessionId,
            (request, call, _) => Task.FromResult(Execution(
                request,
                call == 2 ? RecordedMutationOutcome.AlreadyAppliedNoProviderCall : RecordedMutationOutcome.ExecutedAndApplied)));

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.Completed, result.Status);
        Assert.Equal(new[] { 1, 2, 3 }, executor.Calls.Select(request => request.ExecutionOrder));
        Assert.Equal(3, result.AttemptedRequests.Count);
        Assert.Equal(3, result.CompletedExecutions.Count);
        Assert.Empty(result.NotAttemptedRequests);
        Assert.Equal(0, executor.RevertedCallCount);
    }

    [Theory]
    [InlineData(RecordedMutationOutcome.ExistingPlannedBlockedManualRecoveryRequired)]
    [InlineData(RecordedMutationOutcome.ExistingRevertedRejectedNoProviderCall)]
    [InlineData(RecordedMutationOutcome.MutationFailedRemainsPlanned)]
    [InlineData(RecordedMutationOutcome.ProviderUnsupportedRemainsPlanned)]
    [InlineData(RecordedMutationOutcome.ProviderSkippedRemainsPlanned)]
    [InlineData(RecordedMutationOutcome.ProviderStateChangedExternallyRemainsPlanned)]
    [InlineData(RecordedMutationOutcome.AlreadyRestoredOwnershipNotClaimedRemainsPlanned)]
    [InlineData(RecordedMutationOutcome.ProviderThrewMutationIndeterminateManualRecoveryRequired)]
    [InlineData(RecordedMutationOutcome.PlannedWriteFailedMutationNotAttempted)]
    [InlineData(RecordedMutationOutcome.MutationSucceededOwnershipRecordingFailed)]
    [InlineData(RecordedMutationOutcome.SessionMismatchRejectedNoSideEffects)]
    public async Task EveryUnsafeOutcomeStopsRemainingRequests(RecordedMutationOutcome outcome)
    {
        var operations = new[] { Operation(1), Operation(2), Operation(3) };
        var preparation = await PrepareAsync(operations, operations.Select(operation => Record(operation)).ToArray());
        var executor = new TestExecutor(SessionId, (request, _, _) => Task.FromResult(Execution(request, outcome, requiresManualRecovery: true)));

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.StoppedAfterUnsafeOutcome, result.Status);
        Assert.Single(result.AttemptedRequests);
        Assert.Single(result.CompletedExecutions);
        Assert.Same(result.AttemptedRequests[0], result.StoppedAtRequest);
        Assert.Equal(new[] { 2, 3 }, result.NotAttemptedRequests.Select(request => request.ExecutionOrder));
        Assert.True(result.RequiresManualRecovery);
    }

    [Fact]
    public async Task PreCancellationPropagatesBeforeAnyExecutorCall()
    {
        var operation = Operation(1);
        var preparation = await PrepareAsync(new[] { operation }, new[] { Record(operation) });
        var executor = new TestExecutor(SessionId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(preparation, executor, cancellation.Token));

        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task FirstExecutorCallCancellationReturnsStructuredAttempt()
    {
        var operation = Operation(1);
        var preparation = await PrepareAsync(new[] { operation }, new[] { Record(operation) });
        var executor = new TestExecutor(
            SessionId,
            (_, _, token) => Task.FromCanceled<RecordedMutationExecution>(
                token.IsCancellationRequested ? token : new CancellationToken(canceled: true)));

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.CancelledAfterExecutionAttempt, result.Status);
        Assert.Single(result.AttemptedRequests);
        Assert.Empty(result.CompletedExecutions);
        Assert.Same(result.AttemptedRequests[0], result.StoppedAtRequest);
        Assert.True(result.RequiresManualRecovery);
    }

    [Fact]
    public async Task CancellationBetweenOperationsReturnsStructuredPartialResult()
    {
        var operations = new[] { Operation(1), Operation(2) };
        var preparation = await PrepareAsync(operations, operations.Select(operation => Record(operation)).ToArray());
        using var cancellation = new CancellationTokenSource();
        var executor = new TestExecutor(SessionId, (request, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(Execution(request, RecordedMutationOutcome.ExecutedAndApplied));
        });

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, cancellation.Token);

        Assert.Equal(RestorationBatchExecutionStatus.CancelledAfterPartialExecution, result.Status);
        Assert.Single(result.AttemptedRequests);
        Assert.Single(result.CompletedExecutions);
        Assert.Null(result.StoppedAtRequest);
        Assert.DoesNotContain(result.AttemptedRequests, request => request.ExecutionOrder == 2);
        Assert.Equal(2, result.NotAttemptedRequests[0].ExecutionOrder);
        Assert.Single(result.NotAttemptedRequests);
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task LaterExecutorCallCancellationPreservesEarlierCompletedExecution()
    {
        var operations = new[] { Operation(1), Operation(2), Operation(3) };
        var preparation = await PrepareAsync(operations, operations.Select(operation => Record(operation)).ToArray());
        var executor = new TestExecutor(SessionId, (request, call, _) =>
            call == 1
                ? Task.FromResult(Execution(request, RecordedMutationOutcome.ExecutedAndApplied))
                : Task.FromCanceled<RecordedMutationExecution>(new CancellationToken(canceled: true)));

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.CancelledAfterPartialExecution, result.Status);
        Assert.Equal(2, result.AttemptedRequests.Count);
        Assert.Single(result.CompletedExecutions);
        Assert.Equal(2, result.StoppedAtRequest!.ExecutionOrder);
        Assert.Equal(3, Assert.Single(result.NotAttemptedRequests).ExecutionOrder);
        Assert.True(result.RequiresManualRecovery);
    }

    [Fact]
    public async Task ExecutorThrowAfterEarlierSuccessPreservesPartialHistory()
    {
        var operations = new[] { Operation(1), Operation(2), Operation(3) };
        var preparation = await PrepareAsync(operations, operations.Select(operation => Record(operation)).ToArray());
        var executor = new TestExecutor(SessionId, (request, call, _) =>
            call == 1
                ? Task.FromResult(Execution(request, RecordedMutationOutcome.ExecutedAndApplied))
                : Task.FromException<RecordedMutationExecution>(new InvalidOperationException("boom")));

        var result = await new RestorationExecutionOrchestrator(new TestLedger()).ExecutePreparedAsync(
            preparation, executor, CancellationToken.None);

        Assert.Equal(RestorationBatchExecutionStatus.ExecutorThrewAfterAttempt, result.Status);
        Assert.Equal(2, result.AttemptedRequests.Count);
        Assert.Single(result.CompletedExecutions);
        Assert.Equal(2, result.StoppedAtRequest!.ExecutionOrder);
        Assert.Equal(3, Assert.Single(result.NotAttemptedRequests).ExecutionOrder);
        Assert.True(result.RequiresManualRecovery);
    }

    [Fact]
    public void PublicSurfaceHasNoProviderFactoryCapabilityOrTransactionState()
    {
        var constructor = typeof(RestorationExecutionOrchestrator).GetConstructors().Single();
        Assert.Equal(new[] { typeof(IOwnershipLedger) }, constructor.GetParameters().Select(parameter => parameter.ParameterType));

        var forbiddenTypes = new[]
        {
            typeof(IRecordedMutationExecutorFactory),
            typeof(IRestorationMutationProvider),
            typeof(IServiceProvider)
        };
        Assert.DoesNotContain(
            typeof(RestorationExecutionOrchestrator).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => forbiddenTypes.Contains(field.FieldType));
        Assert.DoesNotContain(
            typeof(RestorationOrchestrationPreparation).GetProperties(),
            property => property.Name.Contains("Transaction", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(RestorationExecutionOrchestrator).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "LibertyRoute.Restoration.Windows");
    }

    [Fact]
    public void ServiceCompositionDoesNotRegisterOrchestrator()
    {
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IRestorationExecutionOrchestrator) ||
                          descriptor.ImplementationType == typeof(RestorationExecutionOrchestrator));
    }
}
