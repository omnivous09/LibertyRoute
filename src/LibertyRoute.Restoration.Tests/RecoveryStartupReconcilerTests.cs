using LibertyRoute.Core;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Restoration.Windows;

namespace LibertyRoute.Restoration.Tests;

public sealed class RecoveryStartupReconcilerTests
{
    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded, null, true)]
    [InlineData(RecoveryPhase.Prepared, OwnedChangeLifecycle.Planned, true)]
    [InlineData(RecoveryPhase.ExecutionStarted, null, true)]
    [InlineData(RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied, false)]
    [InlineData(RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, false)]
    [InlineData(RecoveryPhase.BaselineVerified, OwnedChangeLifecycle.Applied, false)]
    [InlineData(RecoveryPhase.LedgerFinalizing, null, true)]
    [InlineData(RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Applied, false)]
    [InlineData(RecoveryPhase.LedgerFinalized, OwnedChangeLifecycle.Applied, false)]
    public async Task StaleExactJournalExpectationNeverOverwritesReplacement(
        RecoveryPhase phase, OwnedChangeLifecycle? recoveryLifecycle, bool expectsManual)
    {
        var originalLifecycle = phase is RecoveryPhase.LedgerFinalized
            ? OwnedChangeLifecycle.Reverted : OwnedChangeLifecycle.Applied;
        if (phase == RecoveryPhase.LedgerFinalizing && recoveryLifecycle is null)
            originalLifecycle = OwnedChangeLifecycle.Planned;
        await using var fixture = await Fixture.CreateAsync(phase, originalLifecycle, recoveryLifecycle);
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        var replacement = await Fixture.CreateAsync(phase, originalLifecycle, recoveryLifecycle,
            attemptOverride: Guid.Parse("99999999-9999-9999-9999-999999999999"));
        await using (replacement)
        {
            var replacementSnapshot = await replacement.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
            strict.BeforeFirstCas = () => strict.Replace(replacementSnapshot!);
            var result = await new RecoveryStartupReconciler(strict, fixture.Ledger, fixture.Network,
                new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

            Assert.Equal(expectsManual
                ? RecoveryStartupReconciliationStatus.ManualRecoveryPersistenceFailed
                : RecoveryStartupReconciliationStatus.StaleJournal, result.Status);
            Assert.Equal(Guid.Parse("99999999-9999-9999-9999-999999999999"), strict.CurrentAttempt);
            Assert.Equal(1, strict.CasCalls);
        }
    }

    [Fact]
    public async Task StaleLedgerFinalizationDoesNotRetryOrAdvanceJournal()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var strictLedger = await StrictLedger.CreateAsync(fixture.Ledger, Fixture.Session);
        strictLedger.BeforeFirstCas = strictLedger.ReplaceWithValidDifferentRecoveryAttempt;
        var result = await new RecoveryStartupReconciler(fixture.Journal, strictLedger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.StaleLedger, result.Status);
        Assert.Equal(1, strictLedger.CasCalls);
        Assert.DoesNotContain(strictLedger.Records, record =>
            record.Purpose == RecordPurpose.SessionMutation && record.Lifecycle == OwnedChangeLifecycle.Reverted);
        Assert.Equal(RecoveryPhase.LedgerFinalizing,
            (await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None))!.Transaction.RecoveryCompletion!.Phase);
    }

    [Fact]
    public async Task SuccessfulLedgerPublicationWithUnchangedRevisionFailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var strictLedger = await StrictLedger.CreateAsync(fixture.Ledger, Fixture.Session);
        strictLedger.RetainRevisionOnPublication = true;
        var initialRevision = strictLedger.Revision;

        var result = await new RecoveryStartupReconciler(fixture.Journal, strictLedger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.StaleLedger, result.Status);
        Assert.Equal(1, strictLedger.CasCalls);
        Assert.Equal(initialRevision, strictLedger.LastExpectedRevision);
        Assert.Equal(initialRevision, strictLedger.Revision);
        Assert.True(strictLedger.ReadAfterPublicationUsedNonCancelableToken);
        Assert.Equal(2, strictLedger.ReadCalls);
        Assert.Single(strictLedger.LastTransitions);
        Assert.All(strictLedger.LastTransitions, transition =>
        {
            Assert.Equal(OwnedChangeLifecycle.Applied, transition.ExpectedLifecycle);
            Assert.Equal(OwnedChangeLifecycle.Reverted, transition.Proposed.Lifecycle);
            Assert.Equal(RecordPurpose.SessionMutation, transition.Proposed.Purpose);
        });
        Assert.All(strictLedger.Records.Where(record => record.Purpose == RecordPurpose.SessionMutation),
            record => Assert.Equal(OwnedChangeLifecycle.Reverted, record.Lifecycle));
        Assert.Equal(OwnedChangeLifecycle.Applied,
            strictLedger.Records.Single(record => record.Purpose == RecordPurpose.RecoveryMutation).Lifecycle);
        Assert.Equal(RecoveryPhase.LedgerFinalizing,
            (await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None))!.Transaction.RecoveryCompletion!.Phase);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task StaleExecutionCompletedManualCasPreservesValidReplacement()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        await using var replacement = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied,
            attemptOverride: Guid.Parse("55555555-5555-5555-5555-555555555555"));
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        var replacementSnapshot = await replacement.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
        strict.BeforeFirstCas = () => strict.Replace(replacementSnapshot!);

        var result = await new RecoveryStartupReconciler(strict, fixture.Ledger,
            new CountingNetwork(Snapshot()), new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryPersistenceFailed, result.Status);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), strict.CurrentAttempt);
        Assert.Equal(1, strict.CasCalls);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("authorization")]
    [InlineData("change")]
    [InlineData("original")]
    public async Task ValidReplacementLedgerProvenanceNeverAuthorizesAdvancement(string kind)
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var strictLedger = await StrictLedger.CreateAsync(fixture.Ledger, Fixture.Session);
        strictLedger.ReplaceValidIdentity(kind);

        var result = await new RecoveryStartupReconciler(fixture.Journal, strictLedger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal(0, strictLedger.CasCalls);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task CancellationAfterNormalJournalPublicationCannotHidePublishedPhase()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        using var cancellation = new CancellationTokenSource();
        strict.AfterFirstPublication = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoveryStartupReconciler(strict, fixture.Ledger, fixture.Network,
                new RecoveryBaselineVerifier()).ReconcileAsync(cancellation.Token));

        Assert.True(strict.ReadAfterPublicationUsedNonCancelableToken);
        Assert.Equal(RecoveryPhase.ExecutionCompleted, strict.CurrentPhase);
    }

    [Fact]
    public async Task CancellationAfterManualPublicationStillVerifiesAndReportsManual()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.Prepared, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Planned);
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        using var cancellation = new CancellationTokenSource();
        strict.AfterFirstPublication = cancellation.Cancel;

        var result = await new RecoveryStartupReconciler(strict, fixture.Ledger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(cancellation.Token);

        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.True(strict.ReadAfterPublicationUsedNonCancelableToken);
        Assert.Equal(RecoveryPhase.ManualRecoveryRequired, strict.CurrentPhase);
    }

    [Fact]
    public async Task CancellationAfterLedgerPublicationCannotInterruptExactVerification()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var strictLedger = await StrictLedger.CreateAsync(fixture.Ledger, Fixture.Session);
        using var cancellation = new CancellationTokenSource();
        strictLedger.AfterFirstPublication = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoveryStartupReconciler(fixture.Journal, strictLedger, fixture.Network,
                new RecoveryBaselineVerifier()).ReconcileAsync(cancellation.Token));

        Assert.True(strictLedger.ReadAfterPublicationUsedNonCancelableToken);
        Assert.Equal(1, strictLedger.CasCalls);
        Assert.All(strictLedger.Records.Where(r => r.Purpose == RecordPurpose.SessionMutation),
            record => Assert.Equal(OwnedChangeLifecycle.Reverted, record.Lifecycle));
        Assert.Equal(RecoveryPhase.LedgerFinalized,
            (await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None))!.Transaction.RecoveryCompletion!.Phase);
    }

    [Fact]
    public async Task ValidReplacementAfterPublicationIsObservedButNeverDriven()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        await using var replacement = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied,
            attemptOverride: Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        var replacementSnapshot = await replacement.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
        strict.AfterFirstPublication = () => strict.Replace(replacementSnapshot!);

        var result = await new RecoveryStartupReconciler(strict, fixture.Ledger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.StaleJournal, result.Status);
        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), strict.CurrentAttempt);
        Assert.Equal(1, strict.CasCalls);
    }

    [Fact]
    public async Task ValidReplacementBeforeTerminalClearIsNeverDeleted()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.TerminalCommitted, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied);
        await using var replacement = await Fixture.CreateAsync(
            RecoveryPhase.TerminalCommitted, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied,
            attemptOverride: Guid.Parse("66666666-6666-6666-6666-666666666666"));
        var strict = await StrictJournal.CreateAsync(fixture.Journal);
        var replacementSnapshot = await replacement.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
        strict.BeforeClear = () => strict.Replace(replacementSnapshot!);

        var result = await new RecoveryStartupReconciler(strict, fixture.Ledger, fixture.Network,
            new RecoveryBaselineVerifier()).ReconcileAsync(CancellationToken.None);

        Assert.Equal(RecoveryStartupReconciliationStatus.TerminalClearPending, result.Status);
        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), strict.CurrentAttempt);
        Assert.Equal(1, strict.ClearCalls);
    }

    [Fact]
    public async Task SharedCanonicalJournalLeaseBlocksSecondRecoveryContenderWithoutTimingAssumption()
    {
        var path = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Lease", Guid.NewGuid().ToString("N"), "active.lrj");
        var firstJournal = new LeaseJournal(path);
        var secondJournal = new LeaseJournal(path.ToUpperInvariant());
        using var first = await RecoverySessionLease.AcquireAsync(firstJournal, CancellationToken.None);
        var secondAcquire = RecoverySessionLease.AcquireAsync(secondJournal, CancellationToken.None).AsTask();

        Assert.False(secondAcquire.IsCompleted);
        first.Dispose();
        using var second = await secondAcquire;
    }

    [Fact]
    public async Task D1cAndActualD1bWorkflowShareTheFirstAttemptBoundary()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new BlockingRecoveryJournal(
            Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Lease", Guid.NewGuid().ToString("N"), "active.lrj"),
            entered, release);
        var ledger = new EmptyConditionalLedger();
        var network = new CountingNetwork(Snapshot(Route()));
        var d1c = new RecoveryStartupReconciler(journal, ledger, network, new RecoveryBaselineVerifier());
        var d1b = new ControlledRecoveryRestorationWorkflow(
            journal, network, new RestorationExecutionOrchestrator(ledger),
            new RecordedMutationExecutorFactory(ledger));

        var d1cTask = d1c.ReconcileAsync(CancellationToken.None);
        await entered.Task;
        var d1bTask = d1b.ExecuteAsync(new RecoveryRestorationRequest(Fixture.Session), CancellationToken.None);

        Assert.Equal(0, journal.OrdinaryReadCalls);
        Assert.False(d1bTask.IsCompleted);
        release.SetResult();
        Assert.Equal(RecoveryStartupReconciliationStatus.NoJournal, (await d1cTask).Status);
        Assert.Equal(RecoveryRestorationWorkflowStatus.NoUnfinishedRecovery, (await d1bTask).Status);
        Assert.Equal(1, journal.OrdinaryReadCalls);
    }

    [Fact]
    public async Task NoJournalIsInert()
    {
        await using var fixture = await Fixture.CreateAsync(null, null, null);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.NoJournal, result.Status);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task LegacyJournalIsClassifiedWithoutWrite()
    {
        await using var fixture = await Fixture.CreateAsync(null, null, null, legacy: true);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.LegacyRecoveryRequired, result.Status);
        Assert.NotNull(await fixture.Journal.ReadActiveAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(RecoveryPhase.IntentRecorded, null)]
    [InlineData(RecoveryPhase.IntentRecorded, OwnedChangeLifecycle.Planned)]
    [InlineData(RecoveryPhase.Prepared, OwnedChangeLifecycle.Planned)]
    [InlineData(RecoveryPhase.ExecutionStarted, null)]
    [InlineData(RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Planned)]
    public async Task AmbiguousOrPreExecutionStateBecomesManual(
        RecoveryPhase phase,
        OwnedChangeLifecycle? recoveryLifecycle)
    {
        await using var fixture = await Fixture.CreateAsync(phase, OwnedChangeLifecycle.Applied, recoveryLifecycle);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal(phase, (await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None))!
            .Transaction.RecoveryCompletion!.ManualRecoveryOriginPhase);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task ExecutionStartedAppliedReconstructsCompletionThenCapturesAndClears()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ReconciledAndCleared, result.Status);
        Assert.Equal(1, fixture.Network.Calls);
        Assert.Null(await fixture.Journal.ReadActiveAsync(CancellationToken.None));
        var records = await fixture.Ledger.ReadForSessionAsync(Fixture.Session, CancellationToken.None);
        Assert.Equal(OwnedChangeLifecycle.Reverted, records.Single(r => r.Purpose == RecordPurpose.SessionMutation).Lifecycle);
    }

    [Fact]
    public async Task ExecutionStartedAppliedWithoutOriginalFailsClosedBeforeCapture()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, null, OwnedChangeLifecycle.Applied);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Theory]
    [InlineData(RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied)]
    [InlineData(RecoveryPhase.BaselineVerified, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied)]
    [InlineData(RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied)]
    [InlineData(RecoveryPhase.LedgerFinalizing, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied)]
    [InlineData(RecoveryPhase.LedgerFinalized, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied)]
    [InlineData(RecoveryPhase.TerminalCommitted, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied)]
    public async Task SafeBookkeepingCrashBoundariesReachExactClear(
        RecoveryPhase phase,
        OwnedChangeLifecycle original,
        OwnedChangeLifecycle recovery)
    {
        await using var fixture = await Fixture.CreateAsync(phase, original, recovery);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ReconciledAndCleared, result.Status);
        Assert.Equal(phase == RecoveryPhase.ExecutionCompleted ? 1 : 0, fixture.Network.Calls);
    }

    [Fact]
    public async Task CaptureFailurePersistsManualWithoutAdvancingBaseline()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionCompleted, OwnedChangeLifecycle.Applied, OwnedChangeLifecycle.Applied);
        fixture.Network.Throw = true;
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        var completion = (await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None))!
            .Transaction.RecoveryCompletion!;
        Assert.Equal(RecoveryPhase.ExecutionCompleted, completion.ManualRecoveryOriginPhase);
        Assert.Null(completion.BaselineVerifiedAtUtc);
    }

    [Fact]
    public async Task ExistingManualIsPreservedExactly()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ManualRecoveryRequired, OwnedChangeLifecycle.Applied,
            OwnedChangeLifecycle.Planned, manualOrigin: RecoveryPhase.ExecutionStarted);
        var before = await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
        var result = await fixture.ReconcileAsync();
        var after = await fixture.Journal.ReadActiveRecoveryAsync(CancellationToken.None);
        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal(before!.JournalRevision, after!.JournalRevision);
        Assert.Equal(before.Transaction.RecoveryCompletion!.ManualRecoveryOriginPhase,
            after.Transaction.RecoveryCompletion!.ManualRecoveryOriginPhase);
        Assert.Equal(before.Transaction.RecoveryCompletion.FailureReason,
            after.Transaction.RecoveryCompletion.FailureReason);
    }

    [Fact]
    public async Task WrongRecoveryProvenanceNeverReconstructsExecutionCompleted()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.ExecutionStarted, OwnedChangeLifecycle.Applied,
            OwnedChangeLifecycle.Applied, wrongRecoveryAttempt: true);
        var result = await fixture.ReconcileAsync();
        Assert.Equal(RecoveryStartupReconciliationStatus.ManualRecoveryRequired, result.Status);
        Assert.Equal(0, fixture.Network.Calls);
    }

    [Fact]
    public async Task ReconciliationIsRestartIdempotentAfterClear()
    {
        await using var fixture = await Fixture.CreateAsync(
            RecoveryPhase.TerminalCommitted, OwnedChangeLifecycle.Reverted, OwnedChangeLifecycle.Applied);
        Assert.Equal(RecoveryStartupReconciliationStatus.ReconciledAndCleared,
            (await fixture.ReconcileAsync()).Status);
        Assert.Equal(RecoveryStartupReconciliationStatus.NoJournal,
            (await fixture.ReconcileAsync()).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly Guid Session = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly string _root;
        private Fixture(string root, FileTransactionJournal journal, FileOwnershipLedger ledger, CountingNetwork network)
        { _root = root; Journal = journal; Ledger = ledger; Network = network; }
        internal FileTransactionJournal Journal { get; }
        internal FileOwnershipLedger Ledger { get; }
        internal CountingNetwork Network { get; }

        internal static async Task<Fixture> CreateAsync(
            RecoveryPhase? phase,
            OwnedChangeLifecycle? originalLifecycle,
            OwnedChangeLifecycle? recoveryLifecycle,
            bool legacy = false,
            RecoveryPhase? manualOrigin = null,
            bool wrongRecoveryAttempt = false,
            Guid? attemptOverride = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.D1c", Guid.NewGuid().ToString("N"));
            var journal = new FileTransactionJournal(Path.Combine(root, "active.lrj"));
            var ledger = new FileOwnershipLedger(Path.Combine(root, "ownership"));
            var baseline = Snapshot(Route());
            var tx = new NetworkTransaction(Session, ConnectionState.RollbackRequired,
                DateTimeOffset.UnixEpoch, baseline, Array.Empty<OwnedNetworkChange>(), "engine", null);
            if (legacy)
                await journal.WriteAsync(tx, CancellationToken.None);
            else if (phase.HasValue)
            {
                var original = PersistedOwnedChange.Create(Session,
                    Guid.Parse("22222222-2222-2222-2222-222222222222"), DryRunOperationCategory.Route,
                    "2|10.0.0.0|24", RouteValue(Route()), "<absent>", DateTimeOffset.UnixEpoch.AddMinutes(1),
                    1, OwnershipEvidenceSource.MutationLedger, originalLifecycle ?? OwnedChangeLifecycle.Applied);
                var binding = RecoveryOwnershipEvidenceBinding.Create(original);
                var attempt = attemptOverride ?? Guid.Parse("33333333-3333-3333-3333-333333333333");
                var operation = "Route:2|10.0.0.0|24";
                var change = OwnershipIdentity.DeriveChangeId(Session, operation);
                var authorized = RecoveryFingerprinting.ComputeAuthorizedTransactionFingerprint(tx);
                var manifest = RecoveryManifest.Create(attempt, Session, null, authorized, new[] { binding },
                    change, operation, DryRunOperationCategory.Route.ToString(), original.TargetIdentity,
                    original.OriginalValue, original.AppliedValue, 1, new string('C', 64));
                var completion = new RecoveryCompletion(attempt, RecoveryPhase.IntentRecorded, authorized,
                    RecoveryFingerprinting.ComputeRecoveryManifestFingerprint(manifest),
                    RecoveryManifest.FormatCanonicalEvidenceBindings(new[] { binding }), new string('C', 64),
                    DateTimeOffset.UnixEpoch.AddMinutes(2)) { Manifest = manifest };
                var buildTarget = phase == RecoveryPhase.ManualRecoveryRequired ? manualOrigin : phase;
                foreach (var next in new[] { RecoveryPhase.Prepared, RecoveryPhase.ExecutionStarted,
                             RecoveryPhase.ExecutionCompleted, RecoveryPhase.BaselineVerified,
                             RecoveryPhase.LedgerFinalizing, RecoveryPhase.LedgerFinalized,
                             RecoveryPhase.TerminalCommitted })
                {
                    if (completion.Phase == buildTarget) break;
                    completion = completion.WithPhase(next, DateTimeOffset.UnixEpoch.AddMinutes(3 + (int)next));
                }
                if (phase == RecoveryPhase.ManualRecoveryRequired)
                {
                    completion = completion.WithPhase(RecoveryPhase.ManualRecoveryRequired, failureReason: "manual");
                }
                await journal.WriteAsync(tx with { RecoveryCompletion = completion }, CancellationToken.None);
                if (originalLifecycle.HasValue)
                    await ledger.AppendAsync(original with { Lifecycle = originalLifecycle.Value, IsComplete = originalLifecycle == OwnedChangeLifecycle.Applied }, CancellationToken.None);
                if (recoveryLifecycle.HasValue)
                {
                    var recovery = PersistedOwnedChange.Create(Session, change, DryRunOperationCategory.Route,
                        original.TargetIdentity, original.OriginalValue, original.AppliedValue,
                        DateTimeOffset.UnixEpoch.AddMinutes(4), 1, OwnershipEvidenceSource.MutationLedger,
                        recoveryLifecycle.Value, RecordPurpose.RecoveryMutation,
                        wrongRecoveryAttempt ? Guid.NewGuid() : attempt, original.ChangeId);
                    await ledger.AppendAsync(recovery, CancellationToken.None);
                }
            }
            return new Fixture(root, journal, ledger, new CountingNetwork(baseline));
        }

        internal Task<RecoveryStartupReconciliationResult> ReconcileAsync(CancellationToken token = default)
            => new RecoveryStartupReconciler(Journal, Ledger, Network, new RecoveryBaselineVerifier())
                .ReconcileAsync(token);

        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(_root, true); } catch { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StrictJournal : IRecoveryTransactionJournal
    {
        private RecoveryJournalSnapshot? _current;
        private bool _afterPublication;
        private StrictJournal(RecoveryJournalSnapshot? current) => _current = current;
        internal static async Task<StrictJournal> CreateAsync(IRecoveryTransactionJournal source)
            => new(await source.ReadActiveRecoveryAsync(CancellationToken.None));
        public string JournalPath => Path.Combine(Path.GetTempPath(), "LibertyRoute.Tests.Strict", "active.lrj");
        internal Action? BeforeFirstCas { get; set; }
        internal Action? AfterFirstPublication { get; set; }
        internal Action? BeforeClear { get; set; }
        internal int CasCalls { get; private set; }
        internal int ClearCalls { get; private set; }
        internal RecoveryPhase? CurrentPhase => _current?.Transaction.RecoveryCompletion?.Phase;
        internal Guid? CurrentAttempt => _current?.Transaction.RecoveryCompletion?.RecoveryAttemptId;
        internal bool ReadAfterPublicationUsedNonCancelableToken { get; private set; }
        internal void Replace(RecoveryJournalSnapshot replacement) => _current = replacement;
        public Task<RecoveryJournalSnapshot?> ReadActiveRecoveryAsync(CancellationToken token)
        {
            if (_afterPublication)
                ReadAfterPublicationUsedNonCancelableToken |= !token.CanBeCanceled;
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_current);
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token)
            => Task.FromResult(_current?.Transaction);
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token) => throw new NotSupportedException();
        public Task ClearAsync(Guid expectedSessionId, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryAdvanceRecoveryAsync(RecoveryTransitionExpectation expected,
            NetworkTransaction proposed, CancellationToken token)
        {
            CasCalls++;
            var before = _current ?? throw new InvalidOperationException("A current journal is required.");
            ValidateExpectation(before, expected);
            var proposedCompletion = proposed.RecoveryCompletion ?? throw new InvalidOperationException();
            if (proposedCompletion.Phase == RecoveryPhase.ManualRecoveryRequired)
                Assert.Equal(expected.ExpectedPhase, proposedCompletion.ManualRecoveryOriginPhase);
            Assert.Empty(RecoveryCompletion.ValidateImmutableIdentity(before.Transaction, proposed));
            if (CasCalls == 1) BeforeFirstCas?.Invoke();
            if (!Matches(_current, expected)) return Task.FromResult(false);
            _current = new(proposed, NextRevision(_current!.JournalRevision));
            _afterPublication = true;
            if (CasCalls == 1) AfterFirstPublication?.Invoke();
            return Task.FromResult(true);
        }
        public Task<bool> TryClearTerminalRecoveryAsync(Guid session, string authorization,
            Guid attempt, string manifest, CancellationToken token)
        {
            ClearCalls++;
            BeforeClear?.Invoke();
            var c = _current?.Transaction.RecoveryCompletion;
            if (_current?.Transaction.SessionId != session || c?.Phase != RecoveryPhase.TerminalCommitted ||
                c.RecoveryAttemptId != attempt || !StringComparer.Ordinal.Equals(c.AuthorizedTransactionFingerprint, authorization) ||
                !StringComparer.Ordinal.Equals(c.RecoveryManifestFingerprint, manifest))
                return Task.FromResult(false);
            _current = null;
            return Task.FromResult(true);
        }
        private static void ValidateExpectation(RecoveryJournalSnapshot current, RecoveryTransitionExpectation expected)
        {
            Assert.Equal(current.Transaction.SessionId, expected.SessionId);
            Assert.Equal(current.JournalRevision, expected.JournalRevision);
            Assert.Equal(current.Transaction.RecoveryCompletion!.Phase, expected.ExpectedPhase);
            Assert.Equal(current.Transaction.RecoveryCompletion.RecoveryAttemptId, expected.RecoveryAttemptId);
            Assert.Equal(current.Transaction.RecoveryCompletion.AuthorizedTransactionFingerprint, expected.AuthorizedTransactionFingerprint);
            Assert.Equal(current.Transaction.RecoveryCompletion.RecoveryManifestFingerprint, expected.RecoveryManifestFingerprint);
        }
        private static bool Matches(RecoveryJournalSnapshot? current, RecoveryTransitionExpectation expected)
            => current is not null && current.Transaction.SessionId == expected.SessionId &&
               StringComparer.Ordinal.Equals(current.JournalRevision, expected.JournalRevision) &&
               current.Transaction.RecoveryCompletion?.Phase == expected.ExpectedPhase &&
               current.Transaction.RecoveryCompletion?.RecoveryAttemptId == expected.RecoveryAttemptId &&
               StringComparer.Ordinal.Equals(current.Transaction.RecoveryCompletion?.AuthorizedTransactionFingerprint, expected.AuthorizedTransactionFingerprint) &&
               StringComparer.Ordinal.Equals(current.Transaction.RecoveryCompletion?.RecoveryManifestFingerprint, expected.RecoveryManifestFingerprint);
        private static string NextRevision(string revision)
            => (Convert.ToUInt64(revision[^16..], 16) + 1).ToString("X64");
    }

    private sealed class LeaseJournal(string path) : ITransactionJournal
    {
        public string JournalPath { get; } = path;
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token) => throw new NotSupportedException();
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token) => throw new NotSupportedException();
        public Task ClearAsync(Guid expectedSessionId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class BlockingRecoveryJournal(
        string path, TaskCompletionSource entered, TaskCompletionSource release) : IRecoveryTransactionJournal
    {
        private int _recoveryReads;
        public string JournalPath { get; } = path;
        internal int OrdinaryReadCalls { get; private set; }
        public async Task<RecoveryJournalSnapshot?> ReadActiveRecoveryAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref _recoveryReads) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        }
        public Task<NetworkTransaction?> ReadActiveAsync(CancellationToken token)
        { OrdinaryReadCalls++; return Task.FromResult<NetworkTransaction?>(null); }
        public Task WriteAsync(NetworkTransaction transaction, CancellationToken token) => throw new NotSupportedException();
        public Task ClearAsync(Guid session, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryAdvanceRecoveryAsync(RecoveryTransitionExpectation expected, NetworkTransaction proposed, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TryClearTerminalRecoveryAsync(Guid session, string authorization, Guid attempt, string manifest, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class EmptyConditionalLedger : IConditionalOwnershipLedger
    {
        public Task<OwnershipLedgerSnapshot> ReadVersionedAsync(Guid session, CancellationToken token)
            => Task.FromResult(new OwnershipLedgerSnapshot(session, Array.Empty<PersistedOwnedChange>(), new string('0', 64)));
        public Task<bool> TryApplyTransitionsAsync(Guid session, string revision, IReadOnlyList<OwnershipRecordTransition> transitions, CancellationToken token) => throw new NotSupportedException();
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid session, CancellationToken token)
            => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(Array.Empty<PersistedOwnedChange>());
        public Task ClearSessionAsync(Guid session, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid session, Guid change, CancellationToken token) => Task.FromResult(false);
    }

    private sealed class StrictLedger : IConditionalOwnershipLedger
    {
        private List<PersistedOwnedChange> _records;
        private string _revision = 1UL.ToString("X64");
        private bool _afterPublication;
        private readonly Guid _session;
        private StrictLedger(Guid session, IEnumerable<PersistedOwnedChange> records)
        { _session = session; _records = records.ToList(); }
        internal static async Task<StrictLedger> CreateAsync(IOwnershipLedger source, Guid session)
            => new(session, await source.ReadForSessionAsync(session, CancellationToken.None));
        internal Action? BeforeFirstCas { get; set; }
        internal Action? AfterFirstPublication { get; set; }
        internal int CasCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal bool ReadAfterPublicationUsedNonCancelableToken { get; private set; }
        internal IReadOnlyList<PersistedOwnedChange> Records => _records;
        internal string Revision => _revision;
        internal string? LastExpectedRevision { get; private set; }
        internal IReadOnlyList<OwnershipRecordTransition> LastTransitions { get; private set; } = Array.Empty<OwnershipRecordTransition>();
        internal bool RetainRevisionOnPublication { get; set; }
        internal void ReplaceWithValidDifferentRecoveryAttempt()
        {
            var index = _records.FindIndex(r => r.Purpose == RecordPurpose.RecoveryMutation);
            _records[index] = _records[index] with { RecoveryAttemptId = Guid.Parse("88888888-8888-8888-8888-888888888888") };
            _revision = 2UL.ToString("X64");
        }
        internal void ReplaceValidIdentity(string kind)
        {
            if (kind == "original")
            {
                var index = _records.FindIndex(r => r.Purpose == RecordPurpose.SessionMutation);
                _records[index] = _records[index] with { RecordedAtUtc = _records[index].RecordedAtUtc.AddTicks(1) };
            }
            else
            {
                var index = _records.FindIndex(r => r.Purpose == RecordPurpose.RecoveryMutation);
                _records[index] = kind switch
                {
                    "attempt" => _records[index] with { RecoveryAttemptId = Guid.Parse("88888888-8888-8888-8888-888888888888") },
                    "authorization" => _records[index] with { AuthorizationEvidenceId = Guid.Parse("77777777-7777-7777-7777-777777777777") },
                    "change" => _records[index] with { ChangeId = Guid.Parse("66666666-6666-6666-6666-666666666666") },
                    _ => throw new InvalidOperationException()
                };
            }
            _revision = 2UL.ToString("X64");
        }
        public Task<OwnershipLedgerSnapshot> ReadVersionedAsync(Guid session, CancellationToken token)
        {
            Assert.Equal(_session, session);
            ReadCalls++;
            if (_afterPublication) ReadAfterPublicationUsedNonCancelableToken |= !token.CanBeCanceled;
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new OwnershipLedgerSnapshot(session, _records.ToArray(), _revision));
        }
        public Task<bool> TryApplyTransitionsAsync(Guid session, string expectedRevision,
            IReadOnlyList<OwnershipRecordTransition> transitions, CancellationToken token)
        {
            Assert.Equal(_session, session);
            Assert.Equal(_revision, expectedRevision);
            Assert.NotEmpty(transitions);
            CasCalls++;
            LastExpectedRevision = expectedRevision;
            LastTransitions = transitions.ToArray();
            foreach (var transition in transitions)
            {
                var existing = Assert.Single(_records, r => r.ChangeId == transition.ChangeId);
                Assert.Equal(transition.ExpectedLifecycle, existing.Lifecycle);
                Assert.Equal(existing.SessionId, transition.Proposed.SessionId);
                Assert.Equal(existing.ChangeId, transition.Proposed.ChangeId);
                Assert.Equal(existing.Purpose, transition.Proposed.Purpose);
                Assert.Equal(existing.RecoveryAttemptId, transition.Proposed.RecoveryAttemptId);
                Assert.Equal(existing.AuthorizationEvidenceId, transition.Proposed.AuthorizationEvidenceId);
                Assert.True(existing.ImmutableFieldsMatch(transition.Proposed));
            }
            if (CasCalls == 1) BeforeFirstCas?.Invoke();
            if (!StringComparer.Ordinal.Equals(_revision, expectedRevision)) return Task.FromResult(false);
            foreach (var transition in transitions)
            {
                var index = _records.FindIndex(r => r.ChangeId == transition.ChangeId);
                _records[index] = transition.Proposed;
            }
            if (!RetainRevisionOnPublication)
                _revision = (Convert.ToUInt64(_revision[^16..], 16) + 1).ToString("X64");
            _afterPublication = true;
            if (CasCalls == 1) AfterFirstPublication?.Invoke();
            return Task.FromResult(true);
        }
        public Task AppendAsync(PersistedOwnedChange record, CancellationToken token) => throw new NotSupportedException();
        public Task<IReadOnlyList<PersistedOwnedChange>> ReadForSessionAsync(Guid session, CancellationToken token)
            => Task.FromResult<IReadOnlyList<PersistedOwnedChange>>(_records.ToArray());
        public Task ClearSessionAsync(Guid session, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid session, Guid change, CancellationToken token)
            => Task.FromResult(_records.Any(r => r.ChangeId == change));
    }

    private sealed class CountingNetwork(NetworkStateSnapshot snapshot) : INetworkStateManager
    {
        internal int Calls { get; private set; }
        internal bool Throw { get; set; }
        public Task<NetworkStateSnapshot> CaptureStateAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("capture failed");
            return Task.FromResult(snapshot);
        }
        public Task VerifyRestorationAsync(NetworkStateSnapshot original, CancellationToken cancellationToken)
            => throw new InvalidOperationException("D1c must not invoke restoration verification.");
    }

    private static RouteState Route() => new()
    { Destination = "10.0.0.0/24", NextHop = "10.0.0.1", InterfaceIndex = 4, Metric = 1, AddressFamily = "2" };
    private static NetworkStateSnapshot Snapshot(params RouteState[] routes)
        => new(DateTimeOffset.UnixEpoch, "test", Array.Empty<AdapterState>(), routes, Array.Empty<DnsInterfaceState>());
    private static string RouteValue(RouteState route)
        => $"destination={route.Destination};nextHop={route.NextHop};interfaceIndex={route.InterfaceIndex};metric={route.Metric};addressFamily={route.AddressFamily}";
}
