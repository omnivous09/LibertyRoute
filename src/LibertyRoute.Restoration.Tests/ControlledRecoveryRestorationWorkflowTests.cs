using System.Reflection;
using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration;
using LibertyRoute.Restoration.Windows;
using LibertyRoute.Service;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LibertyRoute.Restoration.Tests;

public sealed class ControlledRecoveryRestorationWorkflowTests
{
    private static readonly RouteState BaselineRoute = new()
    {
        Destination = "10.0.0.0/24",
        NextHop = "10.0.0.1",
        InterfaceIndex = 4,
        Metric = 1,
        AddressFamily = "2"
    };

    private sealed class Journal : ITransactionJournal
    {
        public NetworkTransaction? Active { get; set; }
        public Func<int, NetworkTransaction?>? OnRead { get; init; }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int ClearCount { get; private set; }
        public string JournalPath => "fake";
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult(OnRead?.Invoke(ReadCount) ?? Active);
        }
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
        {
            WriteCount++;
            throw new InvalidOperationException("Workflow must not rewrite the journal.");
        }
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
        {
            ClearCount++;
            throw new InvalidOperationException("Workflow must not clear the journal.");
        }
    }

    private sealed class Network : INetworkStateManager
    {
        private readonly NetworkStateSnapshot _snapshot;
        private readonly Func<Task>? _onCapture;
        public Network(NetworkStateSnapshot snapshot, Func<Task>? onCapture = null)
        {
            _snapshot = snapshot;
            _onCapture = onCapture;
        }
        public int CaptureCount { get; private set; }
        public int VerifyCount { get; private set; }
        public async Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            if (_onCapture is not null)
                await _onCapture();
            cancellationToken.ThrowIfCancellationRequested();
            return _snapshot;
        }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
        {
            VerifyCount++;
            throw new InvalidOperationException("Workflow must not verify or mutate recovery state.");
        }
    }

    private sealed class Ledger : IOwnershipLedger
    {
        private readonly IReadOnlyList<PersistedOwnedChange> _records;
        public Ledger(params PersistedOwnedChange[] records) => _records = records;
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(_records.Where(record => record.SessionId == sessionId).ToArray());
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Trigger : IControlledRestorationActivationTrigger
    {
        public int Count { get; private set; }
        public ControlledRestorationTriggerDecision Evaluate(RestorationOrchestrationPreparation preparation)
        {
            Count++;
            return new(ControlledRestorationTriggerStatus.Authorized, "Controlled test authorization.");
        }
    }

    private sealed class Provider : IRestorationMutationProvider
    {
        public int Calls { get; private set; }
        public Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Succeeded, "Fake.", false));
        }
    }

    private sealed class ProviderFactory : IRestorationMutationProviderFactory
    {
        private readonly Provider _provider;
        public ProviderFactory(Provider provider) => _provider = provider;
        public int Count { get; private set; }
        public RestorationExecutionPreflight Create(RestorationExecutionPreparation preparation, Guid activeSessionId, RestorationExecutionCapability? capability)
        {
            Count++;
            Assert.NotNull(capability);
            Assert.True(capability!.IsValid);
            return new(RestorationExecutionGateStatus.Enabled, "Fake provider.", _provider);
        }
    }

    private sealed class Executor : IRecordedMutationExecutor
    {
        public Executor(Guid sessionId) => ActiveSessionId = sessionId;
        public Guid ActiveSessionId { get; }
        public int RevertedCount { get; private set; }
        public Task<RecordedMutationExecution> ExecuteAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new RecordedMutationExecution(request.OperationIdentity, Guid.NewGuid(), request.SessionId, RecordedMutationOutcome.ExecutedAndApplied, null, true, true, false, "Fake."));
        public Task<RecordedMutationExecution> RecordRevertedAsync(Guid changeId, CancellationToken cancellationToken)
        {
            RevertedCount++;
            throw new InvalidOperationException("Reverted recording is forbidden.");
        }
    }

    private sealed class ExecutorFactory : IRecordedMutationExecutorFactory
    {
        public int Count { get; private set; }
        public List<Executor> Executors { get; } = new();
        public IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider)
        {
            Count++;
            var executor = new Executor(activeSessionId);
            Executors.Add(executor);
            return executor;
        }
    }

    private sealed class DelegatingOrchestrator : IRestorationExecutionOrchestrator
    {
        private readonly IRestorationExecutionOrchestrator _inner;
        public DelegatingOrchestrator(IRestorationExecutionOrchestrator inner) => _inner = inner;
        public RestorationBatchExecution? ExactExecution { get; init; }
        public int PrepareCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public List<Guid> TransactionIds { get; } = new();
        public async Task<RestorationOrchestrationPreparation> PrepareAsync(DryRunRestorationResult dryRunResult, Guid activeTransactionId, Guid activeSessionId, CancellationToken cancellationToken)
        {
            PrepareCount++;
            TransactionIds.Add(activeTransactionId);
            return await _inner.PrepareAsync(dryRunResult, activeTransactionId, activeSessionId, cancellationToken);
        }
        public async Task<RestorationBatchExecution> ExecutePreparedAsync(RestorationOrchestrationPreparation preparation, IRecordedMutationExecutor executor, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return ExactExecution ?? await _inner.ExecutePreparedAsync(preparation, executor, cancellationToken);
        }
    }

    private static NetworkStateSnapshot Snapshot(params RouteState[] routes)
        => new(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), routes, Array.Empty<DnsInterfaceState>());

    private static NetworkTransaction Transaction(Guid sessionId, ConnectionState state = ConnectionState.RollbackRequired)
        => new(sessionId, state, DateTimeOffset.UnixEpoch.AddMinutes(1), Snapshot(BaselineRoute), Array.Empty<OwnedNetworkChange>(), "engine", null);

    private static PersistedOwnedChange Applied(Guid sessionId)
        => PersistedOwnedChange.Create(sessionId, Guid.NewGuid(), DryRunOperationCategory.Route,
            "2|10.0.0.0|24",
            "destination=10.0.0.0/24;nextHop=10.0.0.1;interfaceIndex=4;metric=1;addressFamily=2",
            "<absent>", DateTimeOffset.UnixEpoch.AddMinutes(2), 1, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied);

    private static ControlledRecoveryRestorationWorkflow Workflow(
        Journal journal, Network network, IOwnershipLedger ledger,
        IControlledRestorationActivationAuthority? authority = null,
        ProviderFactory? providerFactory = null,
        ExecutorFactory? executorFactory = null,
        DelegatingOrchestrator? orchestrator = null)
    {
        var actualOrchestrator = orchestrator ?? new DelegatingOrchestrator(new RestorationExecutionOrchestrator(ledger));
        return authority is null
            ? new ControlledRecoveryRestorationWorkflow(journal, network, actualOrchestrator, executorFactory ?? new ExecutorFactory())
            : new ControlledRecoveryRestorationWorkflow(journal, network, actualOrchestrator, executorFactory ?? new ExecutorFactory(), authority, providerFactory!);
    }

    [Fact]
    public async Task InvalidMissingWrongAndCompletedJournalStopBeforeCapture()
    {
        var session = Guid.NewGuid();
        var network = new Network(Snapshot());
        Assert.Equal(RecoveryRestorationWorkflowStatus.InvalidRequest,
            (await Workflow(new Journal(), network, new Ledger()).ExecuteAsync(new(Guid.Empty), CancellationToken.None)).Status);
        Assert.Equal(RecoveryRestorationWorkflowStatus.NoUnfinishedRecovery,
            (await Workflow(new Journal(), network, new Ledger()).ExecuteAsync(new(session), CancellationToken.None)).Status);
        Assert.Equal(RecoveryRestorationWorkflowStatus.JournalSessionMismatch,
            (await Workflow(new Journal { Active = Transaction(Guid.NewGuid()) }, network, new Ledger()).ExecuteAsync(new(session), CancellationToken.None)).Status);
        Assert.Equal(RecoveryRestorationWorkflowStatus.RecoveryNotRequired,
            (await Workflow(new Journal { Active = Transaction(session, ConnectionState.Disconnected) }, network, new Ledger()).ExecuteAsync(new(session), CancellationToken.None)).Status);
        Assert.Equal(0, network.CaptureCount);
    }

    [Fact]
    public async Task JournalChangeDuringCaptureFailsClosedBeforePreparation()
    {
        var session = Guid.NewGuid();
        var first = Transaction(session);
        var changed = first with { State = ConnectionState.RestorationFailed };
        var journal = new Journal { OnRead = count => count == 1 ? first : changed };
        var orchestrator = new DelegatingOrchestrator(new RestorationExecutionOrchestrator(new Ledger(Applied(session))));
        var result = await Workflow(journal, new Network(Snapshot()), new Ledger(), orchestrator: orchestrator)
            .ExecuteAsync(new(session), CancellationToken.None);
        Assert.Equal(RecoveryRestorationWorkflowStatus.JournalChanged, result.Status);
        Assert.Equal(0, orchestrator.PrepareCount);
    }

    [Fact]
    public async Task ApprovedCandidateFingerprintMismatchFailsBeforeCurrentStateCapture()
    {
        var session = Guid.NewGuid();
        var network = new Network(Snapshot());
        var result = await Workflow(
                new Journal { Active = Transaction(session) },
                network,
                new Ledger(Applied(session)))
            .ExecuteAsync(new RecoveryRestorationRequest(session, "STALE-CANDIDATE"), CancellationToken.None);

        Assert.Equal(RecoveryRestorationWorkflowStatus.JournalChanged, result.Status);
        Assert.Equal(0, network.CaptureCount);
    }

    [Theory]
    [InlineData(2, "missing", "NoUnfinishedRecovery")]
    [InlineData(3, "missing", "NoUnfinishedRecovery")]
    [InlineData(2, "session", "JournalSessionMismatch")]
    [InlineData(3, "session", "JournalSessionMismatch")]
    [InlineData(2, "completed", "RecoveryNotRequired")]
    [InlineData(3, "completed", "RecoveryNotRequired")]
    [InlineData(2, "payload", "JournalChanged")]
    [InlineData(3, "payload", "JournalChanged")]
    public async Task EveryJournalRereadValidatesSemanticsBeforeComparingCompleteMarker(
        int invalidAtRead,
        string change,
        string expectedStatus)
    {
        var session = Guid.NewGuid();
        var active = Transaction(session);
        var changed = change switch
        {
            "missing" => null,
            "session" => active with { SessionId = Guid.NewGuid() },
            "completed" => active with { State = ConnectionState.Disconnected },
            "payload" => active with { LastError = "Journal payload changed while recovery remains required." },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var journal = new Journal
        {
            OnRead = count => count >= invalidAtRead ? changed : active
        };
        var trigger = new Trigger();
        var result = await Workflow(
                journal,
                new Network(Snapshot()),
                new Ledger(Applied(session)),
                new ControlledRestorationActivationAuthority(trigger),
                new ProviderFactory(new Provider()))
            .ExecuteAsync(new(session), CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status.ToString());
        Assert.Equal(0, trigger.Count);
    }

    [Theory]
    [InlineData(OwnedChangeLifecycle.Planned)]
    [InlineData(OwnedChangeLifecycle.Reverted)]
    public async Task NonAppliedOrMissingOwnershipBlocksBeforeAuthority(OwnedChangeLifecycle lifecycle)
    {
        var session = Guid.NewGuid();
        var record = Applied(session) with { Lifecycle = lifecycle, IsComplete = false };
        var trigger = new Trigger();
        var authority = new ControlledRestorationActivationAuthority(trigger);
        var result = await Workflow(new Journal { Active = Transaction(session) }, new Network(Snapshot()), new Ledger(record), authority, new ProviderFactory(new Provider()))
            .ExecuteAsync(new(session), CancellationToken.None);
        Assert.Equal(RecoveryRestorationWorkflowStatus.PreparationBlocked, result.Status);
        Assert.Equal(0, trigger.Count);
    }

    [Fact]
    public async Task ProductionAuthorityDeniesWithoutProviderExecutorOrJournalMutation()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var network = new Network(Snapshot());
        var executorFactory = new ExecutorFactory();
        var result = await Workflow(journal, network, new Ledger(Applied(session)), executorFactory: executorFactory)
            .ExecuteAsync(new(session), CancellationToken.None);
        Assert.Equal(RecoveryRestorationWorkflowStatus.ActivationDenied, result.Status);
        Assert.Equal(ControlledRestorationActivationStatus.DeniedTriggerUnavailable, result.ActivationStatus);
        Assert.Equal(0, executorFactory.Count);
        Assert.Equal(0, journal.WriteCount);
        Assert.Equal(0, journal.ClearCount);
        Assert.Equal(0, network.VerifyCount);
    }

    [Fact]
    public async Task ControlledChainPreservesExactPhase4EResultAndUsesFreshTransactionMetadata()
    {
        var session = Guid.NewGuid();
        var expected = new RestorationBatchExecution(RestorationBatchExecutionStatus.CancelledAfterPartialExecution,
            Array.Empty<AuthorizedRestorationRequest>(), Array.Empty<RecordedMutationExecution>(), null,
            Array.Empty<AuthorizedRestorationRequest>(), true, "Exact Phase 4E result.");
        var inner = new RestorationExecutionOrchestrator(new Ledger(Applied(session)));
        var orchestrator = new DelegatingOrchestrator(inner) { ExactExecution = expected };
        var provider = new Provider();
        var providerFactory = new ProviderFactory(provider);
        var executors = new ExecutorFactory();
        var result = await Workflow(new Journal { Active = Transaction(session) }, new Network(Snapshot()), new Ledger(),
                new ControlledRestorationActivationAuthority(new Trigger()), providerFactory, executors, orchestrator)
            .ExecuteAsync(new(session), CancellationToken.None);
        Assert.Equal(RecoveryRestorationWorkflowStatus.ExecutionReturned, result.Status);
        Assert.Same(expected, result.Execution!.BatchExecution);
        Assert.True(result.Execution.BatchExecution!.RequiresManualRecovery);
        Assert.NotEqual(Guid.Empty, Assert.Single(orchestrator.TransactionIds));
        Assert.Equal(1, providerFactory.Count);
        Assert.Equal(1, executors.Count);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, Assert.Single(executors.Executors).RevertedCount);
    }

    [Fact]
    public async Task SameSessionLeaseSerializesButDoesNotBlacklistLaterInvocation()
    {
        var session = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = 0;
        var network = new Network(Snapshot(), async () =>
        {
            if (Interlocked.Increment(ref capture) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        var journal = new Journal { Active = Transaction(session) };
        var firstWorkflow = Workflow(journal, network, new Ledger(Applied(session)));
        var secondWorkflow = Workflow(journal, network, new Ledger(Applied(session)));
        var first = firstWorkflow.ExecuteAsync(new(session), CancellationToken.None);
        await entered.Task;
        var second = secondWorkflow.ExecuteAsync(new(session), CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, network.CaptureCount);
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.Equal(RecoveryRestorationWorkflowStatus.ActivationDenied, result.Status));
        Assert.Equal(2, network.CaptureCount);
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotPoisonSessionLease()
    {
        var session = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var network = new Network(Snapshot(), async () => { entered.TrySetResult(); await release.Task; });
        var workflow = Workflow(new Journal { Active = Transaction(session) }, network, new Ledger(Applied(session)));
        var first = workflow.ExecuteAsync(new(session), CancellationToken.None);
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var waiting = workflow.ExecuteAsync(new(session), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task WorkflowIsInternalUnregisteredAndAbsentFromNormalRuntimeCallSites()
    {
        Assert.False(typeof(ControlledRecoveryRestorationWorkflow).IsPublic);
        Assert.DoesNotContain(typeof(ControlledRecoveryRestorationWorkflow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(RestorationExecutionCapability) || field.FieldType == typeof(ControlledRestorationActivationGrant));
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(typeof(ControlledRecoveryRestorationWorkflow)));

        var root = RepoRoot();
        foreach (var project in new[] { "LibertyRoute.Service", "LibertyRoute.Recovery", "LibertyRoute.Desktop", "LibertyRoute.Networking", "LibertyRoute.Engine" })
        {
            var sources = Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            Assert.DoesNotContain(sources, path => File.ReadAllText(path).Contains(nameof(ControlledRecoveryRestorationWorkflow), StringComparison.Ordinal));
        }
        var workflowSource = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Restoration.Windows", "ControlledRecoveryRestorationWorkflow.cs"));
        Assert.DoesNotContain("RecordRevertedAsync", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAsync", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", workflowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", workflowSource, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
