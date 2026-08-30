using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RecoveryExecutionCoordinatorTests
{
    private static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly RouteState BaselineRoute = new()
    {
        Destination = "10.0.0.0/24", NextHop = "10.0.0.1", InterfaceIndex = 4, Metric = 1, AddressFamily = "2"
    };

    [Fact]
    public async Task SuccessHasExactSecurityCriticalOrderAndProvenance()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.ExecuteAsync();

        Assert.Equal(RecoveryExecutionStatus.Completed, result.Status);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(new[]
        {
            "journal IntentRecorded", "ledger Planned", "ledger read Planned", "journal Prepared",
            "journal ExecutionStarted", "provider", "ledger Applied", "ledger read Applied",
            "journal ExecutionCompleted", "capture", "verify", "journal BaselineVerified",
            "journal LedgerFinalizing", "ledger Reverted", "ledger read Reverted",
            "journal LedgerFinalized", "journal TerminalCommitted", "clear"
        }, harness.Events);

        var recovery = Assert.Single(harness.Ledger.Records, record => record.Purpose == RecordPurpose.RecoveryMutation);
        Assert.Equal(OwnedChangeLifecycle.Applied, recovery.Lifecycle);
        Assert.Equal(result.RecoveryAttemptId, recovery.RecoveryAttemptId);
        Assert.Equal(harness.Original.ChangeId, recovery.AuthorizationEvidenceId);
        Assert.Equal(OwnershipIdentity.DeriveChangeId(Session, harness.Request.OperationIdentity), recovery.ChangeId);
        Assert.Equal(harness.Request.CurrentValue, recovery.OriginalValue);
        Assert.Equal(harness.Request.IntendedRestorationValue, recovery.AppliedValue);
        Assert.Equal(OwnedChangeLifecycle.Reverted, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded, 0)]
    [InlineData(RecoveryPhase.Prepared, 0)]
    [InlineData(RecoveryPhase.ExecutionStarted, 0)]
    [InlineData(RecoveryPhase.ExecutionCompleted, 1)]
    [InlineData(RecoveryPhase.BaselineVerified, 1)]
    [InlineData(RecoveryPhase.LedgerFinalizing, 1)]
    [InlineData(RecoveryPhase.LedgerFinalized, 1)]
    [InlineData(RecoveryPhase.TerminalCommitted, 1)]
    public async Task StaleJournalCasNeverReinvokesProviderOrAdvancesReplacement(
        RecoveryPhase failedPhase,
        int expectedProviderCalls)
    {
        var harness = await Harness.CreateAsync();
        harness.Journal.FailPhase = failedPhase;

        var result = await harness.ExecuteAsync();

        Assert.Equal(expectedProviderCalls, harness.Provider.Calls);
        Assert.Equal(1, harness.Journal.Attempts.Count(phase => phase == failedPhase));
        Assert.DoesNotContain(harness.Journal.Attempts.SkipWhile(phase => phase != failedPhase).Skip(1), phase => phase != RecoveryPhase.ManualRecoveryRequired);
        Assert.Equal(failedPhase == RecoveryPhase.IntentRecorded ? RecoveryExecutionStatus.StaleJournal : RecoveryExecutionStatus.ManualRecoveryRequired, result.Status);
        var expectedOriginalLifecycle = failedPhase is RecoveryPhase.LedgerFinalized or RecoveryPhase.TerminalCommitted
            ? OwnedChangeLifecycle.Reverted
            : OwnedChangeLifecycle.Applied;
        Assert.Equal(expectedOriginalLifecycle, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("attempt")]
    [InlineData("manifest")]
    public async Task DifferentValidIdentityObservedOnPostCasRereadCannotDriveLaterExecution(string replacementKind)
    {
        var harness = await Harness.CreateAsync();
        harness.Journal.ReplaceOnRereadAfter = RecoveryPhase.Prepared;
        harness.Journal.ReplacementKind = replacementKind;

        var result = await harness.ExecuteAsync();

        Assert.Equal(0, harness.Provider.Calls);
        Assert.DoesNotContain(RecoveryPhase.ExecutionStarted, harness.Journal.Attempts);
        Assert.Equal(RecoveryPhase.ManualRecoveryRequired, harness.Journal.CurrentPhase);
        Assert.Equal(RecoveryExecutionStatus.ManualRecoveryPersistenceFailed, result.Status);
    }

    [Theory]
    [InlineData(OwnedChangeLifecycle.Planned, 0, false)]
    [InlineData(OwnedChangeLifecycle.Applied, 1, false)]
    [InlineData(OwnedChangeLifecycle.Reverted, 1, true)]
    public async Task StaleLedgerCasFailsClosedWithoutPartialFinalization(
        OwnedChangeLifecycle failedLifecycle,
        int expectedProviderCalls,
        bool baselineWasVerified)
    {
        var harness = await Harness.CreateAsync();
        harness.Ledger.FailLifecycle = failedLifecycle;

        var result = await harness.ExecuteAsync();

        Assert.Equal(expectedProviderCalls, harness.Provider.Calls);
        Assert.Equal(baselineWasVerified ? 1 : 0, harness.Verifier.Calls);
        Assert.Equal(OwnedChangeLifecycle.Applied, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
        if (failedLifecycle == OwnedChangeLifecycle.Applied)
            Assert.DoesNotContain("journal ExecutionCompleted", harness.Events);
        Assert.True(result.Status is RecoveryExecutionStatus.ManualRecoveryRequired or RecoveryExecutionStatus.ManualRecoveryPersistenceFailed);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task ProviderFailureIsAmbiguousAndNeverPromotesOrRetries(string behavior)
    {
        var harness = await Harness.CreateAsync();
        harness.Provider.Behavior = behavior;

        var result = await harness.ExecuteAsync();

        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(RecoveryExecutionStatus.ManualRecoveryRequired, result.Status);
        Assert.DoesNotContain(harness.Ledger.Records,
            record => record.Purpose == RecordPurpose.RecoveryMutation && record.Lifecycle == OwnedChangeLifecycle.Applied);
        Assert.Equal(OwnedChangeLifecycle.Applied, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
    }

    [Fact]
    public async Task ProviderSuccessForDifferentOperationIdentityFailsClosed()
    {
        var harness = await Harness.CreateAsync();
        harness.Provider.Behavior = "wrong-identity";

        var result = await harness.ExecuteAsync();

        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(RecoveryExecutionStatus.ManualRecoveryRequired, result.Status);
        Assert.DoesNotContain("ledger Applied", harness.Events);
        Assert.DoesNotContain("journal ExecutionCompleted", harness.Events);
        Assert.Equal(OwnedChangeLifecycle.Applied, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
    }

    [Fact]
    public async Task SuccessfulManualCasReplacedBeforeRereadIsNotReportedVerified()
    {
        var harness = await Harness.CreateAsync();
        harness.Provider.Behavior = "failure";
        harness.Journal.ReplaceManualBeforeVerification = true;

        var result = await harness.ExecuteAsync();

        Assert.Equal(RecoveryExecutionStatus.ManualRecoveryPersistenceFailed, result.Status);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.NotEqual(result.RecoveryAttemptId, harness.Journal.CurrentAttemptId);
    }

    [Fact]
    public async Task CancellationImmediatelyAfterExecutionStartedIsDeferred()
    {
        using var cancellation = new CancellationTokenSource();
        var harness = await Harness.CreateAsync();
        harness.Journal.AfterPublished = phase =>
        {
            if (phase == RecoveryPhase.ExecutionStarted)
                cancellation.Cancel();
        };

        var result = await harness.ExecuteAsync(cancellation.Token);

        Assert.Equal(RecoveryExecutionStatus.Completed, result.Status);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.False(harness.Provider.ObservedCancellation);
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded)]
    [InlineData(RecoveryPhase.Prepared)]
    public async Task CancellationBeforeExecutionStartedNeverInvokesProvider(RecoveryPhase cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var harness = await Harness.CreateAsync();
        harness.Journal.AfterPublished = phase =>
        {
            if (phase == cancelAfter)
                cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ExecuteAsync(cancellation.Token));

        Assert.Equal(0, harness.Provider.Calls);
        Assert.DoesNotContain("journal ExecutionStarted", harness.Events);
    }

    [Fact]
    public async Task CancellationBeforeIntentLeavesNoDurableAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var harness = await Harness.CreateAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ExecuteAsync(cancellation.Token));

        Assert.Empty(harness.Journal.Attempts);
        Assert.Equal(0, harness.Provider.Calls);
    }

    [Theory]
    [InlineData("missing-original")]
    [InlineData("original-applied")]
    [InlineData("missing-recovery")]
    [InlineData("recovery-reverted")]
    [InlineData("provenance")]
    public async Task InvalidFinalLedgerRereadNeverPersistsLedgerFinalizedOrClears(string corruption)
    {
        var harness = await Harness.CreateAsync();
        harness.Ledger.FinalReadCorruption = corruption;

        var result = await harness.ExecuteAsync();

        Assert.Equal(1, harness.Provider.Calls);
        Assert.DoesNotContain("journal LedgerFinalized", harness.Events);
        Assert.DoesNotContain("clear", harness.Events);
        Assert.True(result.Status is RecoveryExecutionStatus.ManualRecoveryRequired or RecoveryExecutionStatus.ManualRecoveryPersistenceFailed);
    }

    [Theory]
    [InlineData("missing-recovery")]
    [InlineData("recovery-reverted")]
    [InlineData("provenance")]
    public async Task InvalidAppliedRecoveryRereadPreventsExecutionCompleted(string corruption)
    {
        var harness = await Harness.CreateAsync();
        harness.Ledger.AppliedReadCorruption = corruption;

        var result = await harness.ExecuteAsync();

        Assert.Equal(1, harness.Provider.Calls);
        Assert.DoesNotContain("journal ExecutionCompleted", harness.Events);
        Assert.Equal(OwnedChangeLifecycle.Applied, harness.Ledger.Records.Single(record => record.ChangeId == harness.Original.ChangeId).Lifecycle);
        Assert.True(result.Status is RecoveryExecutionStatus.ManualRecoveryRequired or RecoveryExecutionStatus.ManualRecoveryPersistenceFailed);
    }

    [Theory]
    [InlineData(false, "TerminalClearPending")]
    [InlineData(true, "Completed")]
    public async Task TerminalClearResultIsReportedWithoutProviderRetry(bool clearResult, string expected)
    {
        var harness = await Harness.CreateAsync();
        harness.Journal.ClearResult = clearResult;

        var result = await harness.ExecuteAsync();

        Assert.Equal(expected, result.Status.ToString());
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(1, harness.Journal.ClearCalls);
    }

    [Fact]
    public async Task TerminalClearExceptionLeavesTerminalEvidenceAndNeverRetriesProvider()
    {
        var harness = await Harness.CreateAsync();
        harness.Journal.ThrowOnClear = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync());

        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(RecoveryPhase.TerminalCommitted, harness.Journal.CurrentPhase);
        Assert.Equal(1, harness.Journal.ClearCalls);
    }

    [Fact]
    public async Task ExactTerminalClearMismatchDoesNotDeleteReplacement()
    {
        var harness = await Harness.CreateAsync();
        harness.Journal.ReplaceTerminalBeforeClear = true;

        var result = await harness.ExecuteAsync();

        Assert.Equal(RecoveryExecutionStatus.TerminalClearPending, result.Status);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.Equal(RecoveryPhase.TerminalCommitted, harness.Journal.CurrentPhase);
        Assert.NotEqual(result.RecoveryAttemptId, harness.Journal.CurrentAttemptId);
    }

    private sealed class Harness
    {
        private Harness(NetworkTransaction transaction, PersistedOwnedChange original,
            RestorationOrchestrationPreparation preparation, AuthorizedRestorationRequest request)
        {
            Original = original;
            Preparation = preparation;
            Request = request;
            Journal = new ScriptedJournal(transaction, Events);
            Ledger = new ScriptedLedger(original, Events);
            Provider = new ScriptedProvider(Events);
            Network = new ScriptedNetwork(Snapshot(BaselineRoute), Events);
            Verifier = new ScriptedVerifier(Events);
        }

        internal List<string> Events { get; } = new();
        internal PersistedOwnedChange Original { get; }
        internal RestorationOrchestrationPreparation Preparation { get; }
        internal AuthorizedRestorationRequest Request { get; }
        internal ScriptedJournal Journal { get; }
        internal ScriptedLedger Ledger { get; }
        internal ScriptedProvider Provider { get; }
        internal ScriptedNetwork Network { get; }
        internal ScriptedVerifier Verifier { get; }

        internal static async Task<Harness> CreateAsync()
        {
            var transaction = new NetworkTransaction(Session, ConnectionState.RollbackRequired,
                DateTimeOffset.UnixEpoch.AddMinutes(1), Snapshot(BaselineRoute), Array.Empty<OwnedNetworkChange>(), "engine", null);
            var original = PersistedOwnedChange.Create(Session, Guid.Parse("22222222-2222-2222-2222-222222222222"),
                DryRunOperationCategory.Route, "2|10.0.0.0|24", RouteValue(BaselineRoute), "<absent>",
                DateTimeOffset.UnixEpoch.AddMinutes(2), 1, OwnershipEvidenceSource.MutationLedger, OwnedChangeLifecycle.Applied);
            var evidence = new ReadOnlyLedger(original);
            var preparation = await new RestorationExecutionOrchestrator(evidence).PrepareAsync(
                DryRunRestorationExecutor.CreateDryRun(RestorationPlanner.CreatePlan(transaction.Snapshot, Snapshot())),
                Guid.Parse("33333333-3333-3333-3333-333333333333"), Session, CancellationToken.None);
            return new Harness(transaction, original, preparation, Assert.Single(preparation.ExecutionPreparation.AuthorizedRequests));
        }

        internal Task<RecoveryExecutionResult> ExecuteAsync(CancellationToken token = default)
        {
            var coordinator = new RecoveryExecutionCoordinator(Journal, Ledger, Network, Verifier);
            return coordinator.ExecuteAsync(Journal.Snapshot(), Preparation, Request, new string('C', 64), Provider, token);
        }

    }

    private sealed class ScriptedJournal : IRecoveryTransactionJournal
    {
        private NetworkTransaction? _transaction;
        private int _revision = 1;
        private readonly List<string> _events;
        internal ScriptedJournal(NetworkTransaction transaction, List<string> events) { _transaction = transaction; _events = events; }
        public string JournalPath => "coordinator-test-journal";
        internal RecoveryPhase? FailPhase { get; set; }
        internal Action<RecoveryPhase>? AfterPublished { get; set; }
        internal RecoveryPhase? ReplaceOnRereadAfter { get; set; }
        internal string ReplacementKind { get; set; } = "attempt";
        internal bool ReplaceManualBeforeVerification { get; set; }
        internal bool ReplaceTerminalBeforeClear { get; set; }
        private bool _replaceOnNextRead;
        internal bool ClearResult { get; set; } = true;
        internal int ClearCalls { get; private set; }
        internal bool ThrowOnClear { get; set; }
        internal RecoveryPhase? CurrentPhase => _transaction?.RecoveryCompletion?.Phase;
        internal Guid? CurrentAttemptId => _transaction?.RecoveryCompletion?.RecoveryAttemptId;
        internal List<RecoveryPhase> Attempts { get; } = new();
        internal RecoveryJournalSnapshot Snapshot() => new(_transaction!, Revision());
        private string Revision() => _revision.ToString("X64");
        public Task<RecoveryJournalSnapshot?> ReadActiveRecoveryAsync(CancellationToken cancellationToken)
        {
            if (_replaceOnNextRead)
            {
                _replaceOnNextRead = false;
                if (_transaction!.RecoveryCompletion!.Phase != RecoveryPhase.ManualRecoveryRequired)
                    _transaction = _transaction with { RecoveryCompletion = _transaction.RecoveryCompletion.WithPhase(
                        RecoveryPhase.ManualRecoveryRequired, failureReason: "concurrent replacement") };
                ReplaceIdentity(ReplacementKind);
                _revision++;
            }
            return Task.FromResult<RecoveryJournalSnapshot?>(_transaction is null ? null : Snapshot());
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken cancellationToken) => Task.FromResult(_transaction);
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAsync(Guid expectedSessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAdvanceRecoveryAsync(RecoveryTransitionExpectation expected, NetworkTransaction proposed, CancellationToken cancellationToken)
        {
            var phase = proposed.RecoveryCompletion!.Phase;
            Attempts.Add(phase);
            var currentPhase = _transaction!.RecoveryCompletion?.Phase;
            if (_transaction.SessionId != expected.SessionId ||
                !StringComparer.Ordinal.Equals(Revision(), expected.JournalRevision) ||
                currentPhase != expected.ExpectedPhase ||
                _transaction.RecoveryCompletion?.RecoveryAttemptId != expected.RecoveryAttemptId ||
                !StringComparer.Ordinal.Equals(
                    RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(_transaction),
                    expected.AuthorizedTransactionFingerprint) ||
                !StringComparer.Ordinal.Equals(_transaction.RecoveryCompletion?.RecoveryManifestFingerprint,
                    expected.RecoveryManifestFingerprint))
                return Task.FromResult(false);
            if (phase == FailPhase) return Task.FromResult(false);
            if (_transaction.RecoveryCompletion is not null)
                Assert.Empty(RecoveryCompletion.ValidateImmutableIdentity(_transaction, proposed));
            _transaction = proposed;
            _revision++;
            _events.Add($"journal {phase}");
            _replaceOnNextRead = phase == ReplaceOnRereadAfter;
            if (phase == RecoveryPhase.ManualRecoveryRequired && ReplaceManualBeforeVerification)
            {
                _replaceOnNextRead = true;
                ReplacementKind = "attempt";
            }
            AfterPublished?.Invoke(phase);
            return Task.FromResult(true);
        }
        public Task<bool> TryClearTerminalRecoveryAsync(Guid expectedSessionId, string fingerprint, Guid attemptId, string manifestFingerprint, CancellationToken cancellationToken)
        {
            ClearCalls++;
            Assert.Equal(RecoveryPhase.TerminalCommitted, _transaction!.RecoveryCompletion!.Phase);
            _events.Add("clear");
            if (ThrowOnClear) throw new InvalidOperationException("clear failed");
            if (ReplaceTerminalBeforeClear)
            {
                ReplaceTerminalBeforeClear = false;
                ReplaceIdentity("attempt");
                _revision++;
            }
            var completion = _transaction.RecoveryCompletion!;
            if (_transaction.SessionId != expectedSessionId ||
                !StringComparer.Ordinal.Equals(
                    RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(_transaction), fingerprint) ||
                completion.RecoveryAttemptId != attemptId ||
                !StringComparer.Ordinal.Equals(completion.RecoveryManifestFingerprint, manifestFingerprint))
                return Task.FromResult(false);
            if (ClearResult) _transaction = null;
            return Task.FromResult(ClearResult);
        }

        private void ReplaceIdentity(string kind)
        {
            var completion = _transaction!.RecoveryCompletion!;
            var manifest = completion.Manifest!;
            if (kind == "authorization")
                _transaction = _transaction with { LastError = "replacement transaction" };
            var authorized = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(_transaction);
            var attempt = kind == "attempt" ? Guid.NewGuid() : completion.RecoveryAttemptId;
            var replacementManifest = manifest with
            {
                RecoveryAttemptId = attempt,
                AuthorizedTransactionFingerprint = authorized,
                TargetIdentity = kind == "manifest" ? manifest.TargetIdentity + "-replacement" : manifest.TargetIdentity
            };
            var manifestFingerprint = RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(replacementManifest);
            _transaction = _transaction with
            {
                RecoveryCompletion = completion with
                {
                    RecoveryAttemptId = attempt,
                    AuthorizedTransactionFingerprint = authorized,
                    RecoveryManifestFingerprint = manifestFingerprint,
                    Manifest = replacementManifest
                }
            };
        }
    }

    private sealed class ScriptedLedger : IConditionalOwnershipLedger
    {
        private readonly List<string> _events;
        private int _revision = 1;
        private int _readAfterTransition;
        internal ScriptedLedger(PersistedOwnedChange original, List<string> events) { Records = new List<PersistedOwnedChange> { original }; _events = events; }
        internal List<PersistedOwnedChange> Records { get; private set; }
        internal OwnedChangeLifecycle? FailLifecycle { get; set; }
        internal string? FinalReadCorruption { get; set; }
        internal string? AppliedReadCorruption { get; set; }
        private string Revision() => _revision.ToString("X64");
        public Task<OwnershipLedgerSnapshot> ReadVersionedAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            if (_readAfterTransition > 0)
            {
                var last = Records.Any(record => record.Purpose == RecordPurpose.SessionMutation && record.Lifecycle == OwnedChangeLifecycle.Reverted)
                    ? OwnedChangeLifecycle.Reverted
                    : Records.Any(record => record.Purpose == RecordPurpose.RecoveryMutation && record.Lifecycle == OwnedChangeLifecycle.Applied)
                        ? OwnedChangeLifecycle.Applied : OwnedChangeLifecycle.Planned;
                _events.Add($"ledger read {last}");
                _readAfterTransition = 0;
                if (last == OwnedChangeLifecycle.Applied && AppliedReadCorruption is not null)
                    CorruptFinal(AppliedReadCorruption);
                if (last == OwnedChangeLifecycle.Reverted && FinalReadCorruption is not null)
                    CorruptFinal(FinalReadCorruption);
            }
            return Task.FromResult(new OwnershipLedgerSnapshot(sessionId, Records.ToArray(), Revision()));
        }
        public Task<bool> TryApplyTransitionsAsync(Guid sessionId, string expectedRevision, IReadOnlyList<OwnershipRecordTransition> transitions, CancellationToken cancellationToken)
        {
            Assert.Equal(Revision(), expectedRevision);
            Assert.NotEmpty(transitions);
            var lifecycle = transitions[0].Proposed.Lifecycle;
            if (lifecycle == FailLifecycle) return Task.FromResult(false);
            var copy = Records.ToList();
            foreach (var transition in transitions)
            {
                var index = copy.FindIndex(record => record.ChangeId == transition.ChangeId);
                Assert.Equal(transition.ExpectedLifecycle.HasValue, index >= 0);
                if (index >= 0) { Assert.Equal(transition.ExpectedLifecycle, copy[index].Lifecycle); copy[index] = transition.Proposed; }
                else copy.Add(transition.Proposed);
            }
            Records = copy; _revision++; _readAfterTransition = 1; _events.Add($"ledger {lifecycle}");
            return Task.FromResult(true);
        }
        private void CorruptFinal(string kind)
        {
            var original = Records.Single(record => record.Purpose == RecordPurpose.SessionMutation);
            var recovery = Records.Single(record => record.Purpose == RecordPurpose.RecoveryMutation);
            Records = kind switch
            {
                "missing-original" => Records.Where(record => record.ChangeId != original.ChangeId).ToList(),
                "original-applied" => Records.Select(record => record.ChangeId == original.ChangeId ? record with { Lifecycle = OwnedChangeLifecycle.Applied, IsComplete = true } : record).ToList(),
                "missing-recovery" => Records.Where(record => record.ChangeId != recovery.ChangeId).ToList(),
                "recovery-reverted" => Records.Select(record => record.ChangeId == recovery.ChangeId ? record with { Lifecycle = OwnedChangeLifecycle.Reverted, IsComplete = false } : record).ToList(),
                "provenance" => Records.Select(record => record.ChangeId == recovery.ChangeId ? record with { AuthorizationEvidenceId = Guid.NewGuid() } : record).ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken token) => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(Records);
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken token) => throw new NotSupportedException();
        public Task ClearSessionAsync(Guid sessionId, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ScriptedProvider : IRestorationMutationProvider
    {
        private readonly List<string> _events;
        internal ScriptedProvider(List<string> events) => _events = events;
        internal int Calls { get; private set; }
        internal string Behavior { get; set; } = "success";
        internal bool ObservedCancellation { get; private set; }
        public Task<RestorationMutationResult> ApplyAsync(AuthorizedRestorationRequest request, CancellationToken token)
        {
            Calls++; ObservedCancellation = token.IsCancellationRequested; _events.Add("provider");
            return Behavior switch
            {
                "exception" => throw new InvalidOperationException("provider failed"),
                "cancellation" => throw new OperationCanceledException(),
                "failure" => Task.FromResult(new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Failed, "failed", false)),
                "wrong-identity" => Task.FromResult(new RestorationMutationResult(request.OperationIdentity + "-other", RestorationMutationState.Succeeded, "wrong", false)),
                _ => Task.FromResult(new RestorationMutationResult(request.OperationIdentity, RestorationMutationState.Succeeded, "succeeded", false))
            };
        }
    }

    private sealed class ScriptedNetwork : INetworkStateManager
    {
        private readonly NetworkStateSnapshot _snapshot; private readonly List<string> _events;
        internal ScriptedNetwork(NetworkStateSnapshot snapshot, List<string> events) { _snapshot = snapshot; _events = events; }
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken token) { _events.Add("capture"); return Task.FromResult(_snapshot); }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class ScriptedVerifier : IRecoveryBaselineVerifier
    {
        private readonly List<string> _events; internal ScriptedVerifier(List<string> events) => _events = events;
        internal int Calls { get; private set; }
        public RecoveryBaselineVerification Verify(NetworkStateSnapshot baseline, NetworkStateSnapshot fresh, RecoveryManifest manifest)
        { Calls++; _events.Add("verify"); return new(true, "verified"); }
    }

    private sealed class ReadOnlyLedger : IOwnershipLedger
    {
        private readonly PersistedOwnedChange _record; internal ReadOnlyLedger(PersistedOwnedChange record) => _record = record;
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid sessionId, CancellationToken token) => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(new[] { _record });
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken token) => throw new NotSupportedException();
        public Task ClearSessionAsync(Guid sessionId, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid sessionId, Guid changeId, CancellationToken token) => throw new NotSupportedException();
    }

    private static NetworkStateSnapshot Snapshot(params RouteState[] routes)
        => new(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), routes, Array.Empty<DnsInterfaceState>());
    private static string RouteValue(RouteState route)
        => $"destination={route.Destination};nextHop={route.NextHop};interfaceIndex={route.InterfaceIndex};metric={route.Metric};addressFamily={route.AddressFamily}";
}
