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

public sealed class ControlledRecoveryApprovalTests
{
    private sealed class Network : INetworkStateManager
    {
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new NetworkStateSnapshot(DateTimeOffset.UnixEpoch, "test-machine", Array.Empty<AdapterState>(), Array.Empty<RouteState>(), Array.Empty<DnsInterfaceState>()));
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Verification is outside Phase 4J.");
    }

    private sealed class Ledger : IOwnershipLedger
    {
        private readonly PersistedOwnedChange _record;
        public Ledger(PersistedOwnedChange record) => _record = record;
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(_record.SessionId == sessionId ? new[] { _record } : Array.Empty<PersistedOwnedChange>());
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public int Calls { get; private set; }
        public RestorationExecutionPreflight Create(RestorationExecutionPreparation preparation, Guid activeSessionId, RestorationExecutionCapability? capability)
        {
            Calls++;
            Assert.True(capability!.IsValid);
            return new(RestorationExecutionGateStatus.Enabled, "Fake.", _provider);
        }
    }

    private sealed class ExecutorFactory : IRecordedMutationExecutorFactory
    {
        private sealed class Executor(Guid sessionId) : IRecordedMutationExecutor
        {
            public Guid ActiveSessionId { get; } = sessionId;
            public Task<RecordedMutationExecution> ExecuteAsync(AuthorizedRestorationRequest request, CancellationToken cancellationToken)
                => Task.FromResult(new RecordedMutationExecution(request.OperationIdentity, Guid.NewGuid(), request.SessionId, RecordedMutationOutcome.ExecutedAndApplied, null, true, true, false, "Fake."));
            public Task<RecordedMutationExecution> RecordRevertedAsync(Guid changeId, CancellationToken cancellationToken)
                => throw new InvalidOperationException("Reverted recording is forbidden.");
        }

        public int Calls { get; private set; }
        public IRecordedMutationExecutor Create(Guid activeSessionId, IRestorationMutationProvider provider)
        {
            Calls++;
            return new Executor(activeSessionId);
        }
    }
    private sealed class Journal : ITransactionJournal
    {
        public NetworkTransaction? Active { get; set; }
        public Exception? ReadException { get; set; }
        public string JournalPath => "fake";
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadException is not null)
                throw ReadException;
            return Task.FromResult(Active);
        }
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Approval must not write the journal.");
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Approval must not clear the journal.");
    }

    private static NetworkTransaction Transaction(
        Guid sessionId,
        ConnectionState state = ConnectionState.RollbackRequired,
        string? lastError = null)
        => new(
            sessionId,
            state,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            new NetworkStateSnapshot(
                DateTimeOffset.UnixEpoch,
                "test-machine",
                Array.Empty<AdapterState>(),
                Array.Empty<RouteState>(),
                Array.Empty<DnsInterfaceState>()),
            Array.Empty<OwnedNetworkChange>(),
            "engine",
            lastError);

    [Fact]
    public async Task CandidateDiscoveryReturnsOnlyMetadataAndHandlesAbsentCompletedAndCorruptJournal()
    {
        var journal = new Journal();
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        Assert.Equal(ControlledRecoveryCandidateStatus.NoUnfinishedRecovery,
            (await authority.QueryCandidateAsync(CancellationToken.None)).Status);

        journal.Active = Transaction(Guid.NewGuid(), ConnectionState.Disconnected);
        Assert.Equal(ControlledRecoveryCandidateStatus.RecoveryNotRequired,
            (await authority.QueryCandidateAsync(CancellationToken.None)).Status);

        journal.Active = null;
        journal.ReadException = new InvalidDataException("corrupt");
        Assert.Equal(ControlledRecoveryCandidateStatus.InvalidJournal,
            (await authority.QueryCandidateAsync(CancellationToken.None)).Status);

        var properties = typeof(ControlledRecoveryCandidate).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(new[] { "CandidateId", "SessionId", "RecoveryState", "StartedAtUtc", "Reason" }, properties);
        Assert.DoesNotContain(properties, name => name.Contains("Route", StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains("Change", StringComparison.OrdinalIgnoreCase) ||
                                                 name.Contains("Plan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CandidateIdentityIsStableForExactJournalAndChangesWithPayload()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var first = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var duplicate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        Assert.Equal(first.CandidateId, duplicate.CandidateId);

        journal.Active = journal.Active! with { LastError = "changed" };
        var changed = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        Assert.NotEqual(first.CandidateId, changed.CandidateId);
    }

    [Fact]
    public async Task ExactApprovalIssuesOneTicketAndCandidateCannotReplay()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var request = new ControlledRecoveryApprovalRequest(candidate.CandidateId, session);

        var first = await authority.ApproveAsync(request, CancellationToken.None);
        var replay = await authority.ApproveAsync(request, CancellationToken.None);

        Assert.Equal(ControlledRecoveryApprovalStatus.Approved, first.Status);
        Assert.NotNull(first.Ticket);
        Assert.Equal(ControlledRecoveryApprovalStatus.CandidateUnavailable, replay.Status);
    }

    [Fact]
    public async Task ConcurrentApprovalProducesExactlyOneTicket()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var request = new ControlledRecoveryApprovalRequest(candidate.CandidateId, session);

        var decisions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            authority.ApproveAsync(request, CancellationToken.None)));

        Assert.Equal(1, decisions.Count(decision => decision.Status == ControlledRecoveryApprovalStatus.Approved));
        Assert.Equal(1, decisions.Count(decision => decision.Ticket is not null));
    }

    [Theory]
    [InlineData("missing", "JournalChanged")]
    [InlineData("session", "JournalChanged")]
    [InlineData("completed", "RecoveryNotRequired")]
    [InlineData("payload", "JournalChanged")]
    public async Task StaleCandidateFailsClosedAndBurnsApproval(
        string change,
        string expected)
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        journal.Active = change switch
        {
            "missing" => null,
            "session" => Transaction(Guid.NewGuid()),
            "completed" => Transaction(session, ConnectionState.Disconnected),
            "payload" => Transaction(session, lastError: "changed"),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };

        var decision = await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None);
        var replay = await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None);

        Assert.Equal(expected, decision.Status.ToString());
        Assert.Null(decision.Ticket);
        Assert.Equal(ControlledRecoveryApprovalStatus.CandidateUnavailable, replay.Status);
    }

    [Fact]
    public async Task WrongSessionBurnsCandidateAndRequiresFreshDiscovery()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;

        var wrong = await authority.ApproveAsync(new(candidate.CandidateId, Guid.NewGuid()), CancellationToken.None);
        var replay = await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None);

        Assert.Equal(ControlledRecoveryApprovalStatus.CandidateUnavailable, wrong.Status);
        Assert.Equal(ControlledRecoveryApprovalStatus.CandidateUnavailable, replay.Status);
        Assert.NotEqual(candidate.CandidateId,
            (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!.CandidateId);
    }

    [Fact]
    public async Task TicketIsOneShotCancellationSafeAndServiceInstanceBound()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var ticket = (await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None)).Ticket!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => authority.Consume(ticket, cancellation.Token));
        Assert.True(ticket.IsTerminal);
        Assert.Throws<InvalidOperationException>(() => authority.Consume(ticket, CancellationToken.None));

        var freshCandidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var freshTicket = (await authority.ApproveAsync(new(freshCandidate.CandidateId, session), CancellationToken.None)).Ticket!;
        var replacementAuthority = new ControlledRecoveryApprovalAuthority(journal);
        Assert.Throws<InvalidOperationException>(() => replacementAuthority.Consume(freshTicket, CancellationToken.None));
        Assert.True(freshTicket.IsTerminal);
    }

    [Fact]
    public async Task CancellationAfterApprovalReservationBurnsCandidateAndRequiresFreshApproval()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            authority.ApproveAsync(new(candidate.CandidateId, session), cancellation.Token));
        var replay = await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None);
        var fresh = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var approved = await authority.ApproveAsync(new(fresh.CandidateId, session), CancellationToken.None);

        Assert.Equal(ControlledRecoveryApprovalStatus.CandidateUnavailable, replay.Status);
        Assert.NotEqual(candidate.CandidateId, fresh.CandidateId);
        Assert.Equal(ControlledRecoveryApprovalStatus.Approved, approved.Status);
        approved.Ticket!.Dispose();
    }

    [Fact]
    public async Task ApprovalProofAuthorizesExactSessionOnce()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var ticket = (await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None)).Ticket!;
        var proof = authority.Consume(ticket, CancellationToken.None);
        var trigger = new ApprovedRecoveryActivationTrigger(proof);
        var preparation = Preparation(session);
        proof.BindPreparation(preparation);

        Assert.Equal(ControlledRestorationTriggerStatus.Authorized, trigger.Evaluate(preparation).Status);
        Assert.Equal(ControlledRestorationTriggerStatus.Denied, trigger.Evaluate(preparation).Status);
    }

    [Fact]
    public async Task ApprovalProofBurnsOnSameSessionPreparationFingerprintMismatch()
    {
        var session = Guid.NewGuid();
        var journal = new Journal { Active = Transaction(session) };
        var authority = new ControlledRecoveryApprovalAuthority(journal);
        var candidate = (await authority.QueryCandidateAsync(CancellationToken.None)).Candidate!;
        var ticket = (await authority.ApproveAsync(new(candidate.CandidateId, session), CancellationToken.None)).Ticket!;
        var proof = authority.Consume(ticket, CancellationToken.None);
        var expected = Preparation(session);
        var different = Preparation(session);
        proof.BindPreparation(expected);
        var trigger = new ApprovedRecoveryActivationTrigger(proof);

        Assert.Equal(ControlledRestorationTriggerStatus.Denied, trigger.Evaluate(different).Status);
        Assert.Equal(ControlledRestorationTriggerStatus.Denied, trigger.Evaluate(expected).Status);
    }

    [Fact]
    public void ControlledApprovedExecutionHasOnlyDurableD1bConstructionPath()
    {
        var constructor = Assert.Single(typeof(ControlledApprovedRecoveryExecution)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert.Contains(typeof(IRecoveryTransactionJournal), parameters);
        Assert.Contains(typeof(IConditionalOwnershipLedger), parameters);
        Assert.DoesNotContain(typeof(ITransactionJournal), parameters);
    }

    [Fact]
    public async Task ApprovalBoundaryIsInternalUnregisteredAndAbsentFromNormalRuntime()
    {
        Assert.False(typeof(ControlledRecoveryApprovalAuthority).IsPublic);
        Assert.False(typeof(ControlledRecoveryApprovalTicket).IsPublic);
        Assert.False(typeof(ApprovedRecoveryActivationTrigger).IsPublic);
        var services = new ServiceCollection();
        services.AddLibertyRouteCoreServices();
        await using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService(typeof(ControlledRecoveryApprovalAuthority)));
        Assert.Null(provider.GetService(typeof(ControlledRecoveryApprovalTicket)));

        var root = RepoRoot();
        foreach (var project in new[] { "LibertyRoute.Service", "LibertyRoute.Recovery", "LibertyRoute.Desktop", "LibertyRoute.Networking", "LibertyRoute.Engine" })
        {
            var sources = Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                               !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
            Assert.DoesNotContain(sources, path => File.ReadAllText(path).Contains(nameof(ControlledRecoveryApprovalAuthority), StringComparison.Ordinal));
        }

        var source = File.ReadAllText(Path.Combine(root, "src", "LibertyRoute.Restoration.Windows", "ControlledRecoveryApproval.cs"));
        Assert.DoesNotContain("RecordRevertedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IConfiguration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source, StringComparison.Ordinal);
    }

    private static RestorationOrchestrationPreparation Preparation(Guid session)
    {
        var operation = new DryRunRestorationOperation(DryRunOperationCategory.Route, DryRunAction.RestoreBaseline,
            "route", "baseline", "applied", "test", 1, true, true, DryRunSafetyState.SafeToPlan);
        var evidence = new OwnershipEvidence(session, operation.Category, operation.TargetIdentity,
            operation.OriginalValue, operation.CurrentValue, Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            operation.ExecutionOrder, OwnershipEvidenceSource.TestFixture, true);
        var dryRun = new DryRunRestorationResult(new[] { operation },
            new DryRunRestorationSummary(1, 1, 1, 0, 0, true, Array.Empty<string>()));
        var authorization = RestorationAuthorizationPolicy.AuthorizeBatch(dryRun, new[] { evidence }, session);
        return new RestorationOrchestrationPreparation(
            session,
            RestorationExecutionPreparation.Prepare(authorization, Guid.NewGuid(), session));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LibertyRoute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
